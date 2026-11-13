from __future__ import annotations
import csv, json, math, re, statistics
from bisect import bisect_right
from pathlib import Path

def rows(path):
    with path.open(newline='', encoding='utf-8-sig') as f: return list(csv.DictReader(f))
def percentile(values,p):
    xs=sorted(values)
    if not xs:return None
    i=(len(xs)-1)*p; lo=int(i); hi=min(lo+1,len(xs)-1)
    return xs[lo]+(xs[hi]-xs[lo])*(i-lo)
def max_integer(rows_,key):
    values=[]
    for row in rows_:
        try:
            value=row.get(key,'').strip()
            if value and value.lower()!='null': values.append(int(value))
        except (ValueError,TypeError):
            continue
    return max(values) if values else None
def read(run):
    p=Path(run); meta=json.loads((p/'run.meta.json').read_text(encoding='utf-8-sig'))
    if not meta.get('performance_phase') or not meta['performance_phase'].get('Rtf'):
        log=(p/'godot-stdout.log').read_text(encoding='utf-8-sig',errors='replace')
        m=re.search(r'(?m)^PERF_PHASE_END .*?mono_ticks=(\d+).*?wall_elapsed_ns=(\d+).*?committed_sim_ns=(\d+).*?rtf=([0-9.]+)',log)
        if m: meta['performance_phase']={'MonoTicks':int(m.group(1)),'WallElapsedNs':int(m.group(2)),'CommittedSimNs':int(m.group(3)),'Rtf':float(m.group(4))}
    return p,meta,rows(p/'godot-ticks.csv'),rows(p/'controller-commands.csv'),rows(p/'godot-frames.csv'),rows(p/'process-samples.csv'),(p/'godot-stdout.log').read_text(encoding='utf-8-sig',errors='replace')
def process_summary(rows_):
    groups={}
    for r in rows_: groups.setdefault(r['process_name'].lower(),[]).append(r)
    out={}
    for name,rs in groups.items():
        cpus=[float(r['cpu_percent_host']) for r in rs if r.get('cpu_percent_host') not in ('','null')]
        working_set_max=max_integer(rs,'working_set_bytes'); private_max=max_integer(rs,'private_bytes')
        out[name]={'cpu_host_p95':percentile(cpus,.95),'working_set_max_mb':None if working_set_max is None else working_set_max/1e6,'private_max_mb':None if private_max is None else private_max/1e6}
    return out
def main(exact_dir,fast_dir,out):
    ep,em,et,ec,ef,er,es=read(exact_dir); fp,fm,ft,fc,ff,fr,fs=read(fast_dir)
    exact_worker=rows(ep/'robot-service-ticks.csv') if (ep/'robot-service-ticks.csv').exists() else []
    fast_worker=rows(fp/'deterministic-model-ticks.csv') if (fp/'deterministic-model-ticks.csv').exists() else []
    if abs(int(em['performance_phase']['CommittedSimNs'])-int(fm['performance_phase']['CommittedSimNs']))>1_000_000: raise SystemExit('runs do not share a simulation horizon within 1 ms')
    e_mot=[(r['sequence'],r['command'],r['reply']) for r in ec if r['event']=='motor']; f_mot=[(r['sequence'],r['command'],r['reply']) for r in fc if r['event']=='motor']
    if e_mot!=f_mot: raise SystemExit('external motor command/reply sequences differ')
    def sensor(commands):
        result=[]
        for r in commands:
            if r['event']=='sensor_query':
                m=re.fullmatch(r'DIST mm=(\d+) valid=1',r['reply'])
                if not m: raise SystemExit(f"unsupported sensor response {r['reply']}")
                result.append((int(r['sequence']),int(m.group(1))))
        return result
    se,sf=sensor(ec),sensor(fc); smap=dict(sf); deltas=[abs(a-smap[i]) for i,a in se if i in smap]
    if len(se)!=len(sf) or len(deltas)!=len(se): raise SystemExit('sensor query sequences differ')
    def sensor_raycast_agreement(commands,ticks,stdout,frequency):
        samples=[(int(t),float(d)*1000.0) for t,d in re.findall(r'GODOT_DISTANCE_SAMPLE sim_time_ns=(\d+) meters=([0-9.]+)',stdout)]
        tick_host_ns=[int(r['mono_ticks'])*1_000_000_000//frequency for r in ticks]
        observations=[]
        by_sequence={r['sequence']:r for r in commands if r['event']=='motor'}
        for query in commands:
            if query['event']!='sensor_query': continue
            motor=by_sequence.get(query['sequence'])
            if motor is None or not query.get('mono_ns'): continue
            sent_ns=int(query['mono_ns']); index=bisect_right(tick_host_ns,sent_ns)-1
            if index<0: continue
            observed_sim_ns=int(ticks[index]['sim_time_ns'])
            prior=[sample for sample in samples if sample[0]<=observed_sim_ns]
            if not prior: continue
            ray_time_ns,ray_mm=prior[-1]
            reply_match=re.fullmatch(r'DIST mm=(\d+) valid=1',query['reply'])
            if reply_match is None: continue
            reply_mm=int(reply_match.group(1))
            observations.append({'sequence':query['sequence'],'observed_sim_ns':observed_sim_ns,
                'raycast_sample_sim_ns':ray_time_ns,'raycast_distance_mm':ray_mm,
                'uart_reply_mm':reply_mm,'reply_minus_logged_raycast_mm':reply_mm-ray_mm})
        errors=[abs(item['reply_minus_logged_raycast_mm']) for item in observations]
        return {'measurement':'UART sensor reply minus the most recent logged Godot raycast sample at or before the command-send simulation boundary; stdout samples are logged every 100 ms simulation time',
            'observations':observations,'absolute_error_p95_mm':percentile(errors,.95),'absolute_error_max_mm':max(errors,default=None)}
    fts=[int(r['sim_time_ns']) for r in ft]
    positions=[]
    for e in et:
        t=int(e['sim_time_ns']); j=bisect_right(fts,t)-1
        if j>=0: positions.append(math.hypot(float(e['chassis_x_m'])-float(ft[j]['chassis_x_m']),float(e['chassis_z_m'])-float(ft[j]['chassis_z_m'])))
    # Compare controller-visible duty at equal simulation times. Tick rows are
    # not index-aligned across backends when their initial phase differs.
    duty_pairs=[]
    for e in et:
        j=bisect_right(fts,int(e['sim_time_ns']))-1
        if j>=0: duty_pairs.append((e,ft[j]))
    duty_mismatches=sum((e['left_duty'],e['right_duty'])!=(f['left_duty'],f['right_duty']) for e,f in duty_pairs)
    def actuator_events(text):
        return [(m.group(1),m.group(2),float(m.group(3))) for line in text.splitlines() if (m:=re.search(r'ACTUATION_TRACE .*?sequence=(\d+) actuator=([^ ]+) duty=(-?[0-9.]+)',line))]
    def timed_actuator_events(text):
        return [(int(m.group(1)),m.group(2),float(m.group(3))) for line in text.splitlines() if (m:=re.search(r'ACTUATION_TRACE tick=(\d+) .*? actuator=([^ ]+) duty=(-?[0-9.]+)',line))]
    ea,fa=actuator_events(es),actuator_events(fs)
    eta,fta=timed_actuator_events(es),timed_actuator_events(fs)
    # Exact emits two initial zero-valued samples before the first command;
    # these do not represent a commanded transition. Keep all later stops.
    if len(eta)>=2 and eta[0][2]==eta[1][2]==0: eta=eta[2:]
    # Keep the direction. A magnitude-only delta can hide whether Fast responds
    # early or late, which matters for causality and matched actuation timing.
    actuator_pairs=[(e[0],f[0]) for e,f in zip(eta,fta) if e[1:]==f[1:]]
    actuator_deltas=[f-e for e,f in actuator_pairs]
    e_targets=[int(r['target_sim_ns']) for r in ec if r['event']=='motor' and r.get('target_sim_ns')]
    f_targets=[int(r['target_sim_ns']) for r in fc if r['event']=='motor' and r.get('target_sim_ns')]
    target_deltas=[f-e for e,f in zip(e_targets,f_targets)]
    def frames(rows_): return [1e9/int(r['frame_interval_ns']) for r in rows_ if int(r['frame_interval_ns'])>0]
    epfps, fpfps=frames(ef),frames(ff)
    def command_motion_latencies(commands,frame_rows,frequency):
        frame_host_ns=[int(r['mono_ticks'])*1_000_000_000//int(frequency) for r in frame_rows]
        observations=[]
        for c in commands:
            if c['event']!='motor' or not c.get('mono_ns'): continue
            sent=int(c['mono_ns']); base=bisect_right(frame_host_ns,sent)-1
            if base<0: continue
            x0=float(frame_rows[base]['chassis_x_m']); z0=float(frame_rows[base]['chassis_z_m']); latency=None
            for j in range(base+1,len(frame_rows)):
                if math.hypot(float(frame_rows[j]['chassis_x_m'])-x0,float(frame_rows[j]['chassis_z_m'])-z0)>=0.001:
                    latency=(frame_host_ns[j]-sent)/1e6; break
            observations.append({'sequence':c['sequence'],'command':c['command'],'latency_to_1mm_motion_ms':latency})
        values=[r['latency_to_1mm_motion_ms'] for r in observations if r['latency_to_1mm_motion_ms'] is not None]
        drive_values=[r['latency_to_1mm_motion_ms'] for r in observations if r['latency_to_1mm_motion_ms'] is not None and (m:=re.fullmatch(r'MOTOR (-?\d+) (-?\d+)',r['command'])) and (int(m.group(1))!=0 or int(m.group(2))!=0)]
        first_drive=next((r for r in observations if r['sequence']=='0'),None)
        return {'threshold_mm':1,'observations':observations,'measured_count':len(values),'p50_ms':percentile(values,.5),'p95_ms':percentile(values,.95),'max_ms':max(values,default=None),'nonzero_drive_command_count':len(drive_values),'nonzero_drive_p50_ms':percentile(drive_values,.5),'nonzero_drive_p95_ms':percentile(drive_values,.95),'first_drive_command_latency_ms':None if first_drive is None else first_drive['latency_to_1mm_motion_ms']}
    def command_sim_time_delivery(commands,ticks,frequency):
        tick_host_ns=[int(r['mono_ticks'])*1_000_000_000//frequency for r in ticks]
        observations=[]
        for command in commands:
            if command['event']!='motor' or not command.get('target_sim_ns') or not command.get('mono_ns'): continue
            sent_ns=int(command['mono_ns']); index=bisect_right(tick_host_ns,sent_ns)-1
            if index<0: continue
            target=int(command['target_sim_ns']); observed=int(ticks[index]['sim_time_ns'])
            observations.append({'sequence':command['sequence'],'target_sim_ns':target,'observed_sim_ns_at_send':observed,'send_lateness_ns':observed-target})
        values=[item['send_lateness_ns'] for item in observations]
        return {'observations':observations,'p50_lateness_ms':None if not values else percentile(values,.5)/1e6,'p95_lateness_ms':None if not values else percentile(values,.95)/1e6,'max_lateness_ms':None if not values else max(values)/1e6}
    exact_names={r['process_name'].lower() for r in er}; fast_names={r['process_name'].lower() for r in fr}
    package_fields=('executable_sha256','managed_assembly_sha256','package_manifest_sha256','main_cs_sha256')
    same_package=(all(em.get(key)==fm.get(key) for key in package_fields)
                  and bool(em.get('executable_sha256')) and bool(em.get('package_manifest_sha256')))
    render_fields=('render_fps_cap','resolution','headless','vsync_enabled','effective_vsync_mode')
    same_render_config=all(em.get(key)==fm.get(key) for key in render_fields)
    same_sensor_environment=em.get('sensor_obstacle_z_sequence_m')==fm.get('sensor_obstacle_z_sequence_m')
    same_firmware=em.get('firmware_sha256')==fm.get('firmware_sha256')
    same_clock_epoch=em.get('clock_epoch_sim_ns')==fm.get('clock_epoch_sim_ns')
    same_rover_commands=em['robot_id']==fm['robot_id']=='rover-01' and e_mot==f_mot
    exact_godot_ids=[int(row['tick_id']) for row in et]
    fast_godot_ids=[int(row['tick_id']) for row in ft]
    exact_worker_ids=[int(row['tick_id']) for row in exact_worker]
    fast_worker_ids=[int(row['tick_id']) for row in fast_worker]
    exact_godot_ends={int(row['tick_id']):int(row['sim_time_ns']) for row in et}
    fast_godot_ends={int(row['tick_id']):int(row['sim_time_ns']) for row in ft}
    exact_worker_boundaries=bool(exact_worker) and all(
        int(row['sim_time_ns'])==exact_godot_ends.get(int(row['tick_id'])) for row in exact_worker)
    fast_epoch=int(fm.get('clock_epoch_sim_ns',0))
    fast_worker_boundaries=bool(fast_worker) and all(
        int(row['sim_time_ns'])==(fast_epoch if int(row['tick_id'])==1 else fast_godot_ends.get(int(row['tick_id'])-1))
        for row in fast_worker)
    exact_worker_trace_is_godot_prefix=(exact_worker_ids==exact_godot_ids[:len(exact_worker_ids)])
    tick_streams_aligned=(exact_godot_ids==fast_godot_ids and fast_worker_ids==fast_godot_ids
                          and exact_worker_trace_is_godot_prefix)
    matched=(e_targets==f_targets and same_package and same_render_config
             and same_sensor_environment and same_firmware and same_clock_epoch and same_rover_commands
             and tick_streams_aligned and exact_worker_boundaries and fast_worker_boundaries)
    result={
      'status':'MATCHED_SCENARIO_SCHEDULE_AND_HORIZON' if matched else 'UNMATCHED_SCENARIO_OR_SCHEDULE',
      'exact_run':em['run_id'],'fast_run':fm['run_id'],
      'same_loaded_rover_and_motor_commands':same_rover_commands,
      'same_package_hashes':same_package,
      'same_render_configuration':same_render_config,
      'same_sensor_environment':same_sensor_environment,
      'same_firmware_hashes':same_firmware,
      'same_authoritative_clock_epoch':same_clock_epoch,
      'command_schedule':{'same_target_sim_times':e_targets==f_targets,'exact_target_sim_ns':e_targets,'fast_target_sim_ns':f_targets,'fast_minus_exact_target_delta_ns':target_deltas,'max_abs_target_delta_ns':max((abs(x) for x in target_deltas),default=None)},
      'sim_horizon_ns':em['performance_phase']['CommittedSimNs'],
      'rtf':{'exact':em['performance_phase']['Rtf'],'fast':fm['performance_phase']['Rtf'],'improvement_ratio':float(fm['performance_phase']['Rtf'])/float(em['performance_phase']['Rtf'])},
      'fps':{'exact_p50':percentile(epfps,.5),'exact_p05':percentile(epfps,.05),'fast_p50':percentile(fpfps,.5),'fast_p05':percentile(fpfps,.05)},
      'command_to_visible_motion':{'measurement':'first Godot _Process frame sampling the root chassis RigidBody3D GlobalPosition at least 1 mm from the last frame before the UART command; this samples the displayed body pose immediately before render, using the recorded shared QPC timebase','exact':command_motion_latencies(ec,ef,em['stopwatch_frequency']),'fast':command_motion_latencies(fc,ff,fm['stopwatch_frequency'])},
      'simulation_time_command_delivery':{'measurement':'latest committed Godot simulation tick visible to the external controller when it sent each command, minus the controller planned target simulation time','exact':command_sim_time_delivery(ec,et,em['stopwatch_frequency']),'fast':command_sim_time_delivery(fc,ft,fm['stopwatch_frequency'])},
      'motor_commands':len(e_mot),'actuator_outputs':{'exact_count':len(ea),'fast_count':len(fa),'sequence_and_signed_duty_match':len(ea)==len(fa) and all(x[:2]==y[:2] and abs(x[2]-y[2])<1e-9 for x,y in zip(ea,fa)),'commanded_transition_values_match_ignoring_initial_zero_samples':len(eta)==len(fta) and all(e[1:]==f[1:] for e,f in zip(eta,fta)),'fast_minus_exact_transition_tick_delta_median':percentile(actuator_deltas,.5),'fast_minus_exact_transition_tick_delta_min':min(actuator_deltas,default=None),'fast_minus_exact_transition_tick_delta_max':max(actuator_deltas,default=None),'duty_alignment':'latest Fast committed tick at or before each Exact tick simulation time','duty_aligned_sample_count':len(duty_pairs),'tick_duty_mismatch_count':duty_mismatches},
      'sensor_replies_mm':{'exact':se,'fast':sf,'absolute_error_p95_mm':percentile(deltas,.95),'absolute_error_max_mm':max(deltas,default=None)},
      'sensor_reply_to_live_raycast':{'exact':sensor_raycast_agreement(ec,et,es,em['stopwatch_frequency']),'fast':sensor_raycast_agreement(fc,ft,fs,fm['stopwatch_frequency'])},
      'trajectory_xz_error_m':{'max':max(positions,default=None),'rms':math.sqrt(statistics.fmean(x*x for x in positions)) if positions else None,'final':positions[-1] if positions else None,'aligned_samples':len(positions)},
      'clock_and_ticks':{'exact_contiguous':len(et)>1 and all(int(b['tick_id'])==int(a['tick_id'])+1 for a,b in zip(et,et[1:])),'fast_contiguous':len(ft)>1 and all(int(b['tick_id'])==int(a['tick_id'])+1 for a,b in zip(ft,ft[1:])),'exact_worker_contiguous':len(exact_worker_ids)>1 and all(b==a+1 for a,b in zip(exact_worker_ids,exact_worker_ids[1:])),'fast_worker_contiguous':len(fast_worker_ids)>1 and all(b==a+1 for a,b in zip(fast_worker_ids,fast_worker_ids[1:])),'exact_worker_tick_count':len(exact_worker_ids),'fast_worker_tick_count':len(fast_worker_ids),'godot_tick_count_exact_fast':[len(et),len(ft)],'exact_worker_trace_is_godot_prefix':exact_worker_trace_is_godot_prefix,'exact_worker_trace_complete':exact_worker_ids==exact_godot_ids,'tick_streams_aligned':tick_streams_aligned,'exact_service_godot_boundaries_exact':exact_worker_boundaries,'fast_model_godot_interval_start_boundaries_exact':fast_worker_boundaries,'exact_renode_present':any('renode' in n for n in exact_names),'fast_renode_present':any('renode' in n for n in fast_names)},
      'cpu_memory_processes':{'exact':process_summary(er),'fast':process_summary(fr)},
    }
    Path(out).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result,indent=2))
if __name__=='__main__':
 import sys
 main(sys.argv[1],sys.argv[2],sys.argv[3])
