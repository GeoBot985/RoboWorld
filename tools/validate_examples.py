#!/usr/bin/env python3
"""Validate RoboWorld examples against JSON Schemas and basic cross-file semantics.

Requires: pip install jsonschema
Usage: python tools/validate_examples.py
Does not replace runtime semantic, graph/port, firmware-peripheral or physics tests.
"""
from __future__ import annotations
import json
import math
import sys
from pathlib import Path
import jsonschema

ROOT = Path(__file__).resolve().parents[1]
SCH = ROOT / "contracts" / "schemas"
EX = ROOT / "examples"
MAPPING = {
    "component": ("component.schema.json", list((EX / "components").glob("*.component.json"))),
    "robot": ("robot.schema.json", list((EX / "robots").glob("*.robot.json"))),
    "world": ("world.schema.json", list((EX / "worlds").glob("*.world.json"))),
    "project": ("project.schema.json", [EX / "project.json"]),
    "ai": ("ai-scene-request.schema.json", [EX / "ai-scene-request.json"]),
}

def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))

def require(ok: bool, error: str):
    if not ok:
        raise ValueError(error)

def finite_floats(obj, path: str):
    if isinstance(obj, dict):
        for k, v in obj.items(): finite_floats(v, f"{path}.{k}")
    elif isinstance(obj, list):
        for i, v in enumerate(obj): finite_floats(v, f"{path}[{i}]")
    elif isinstance(obj, float):
        require(math.isfinite(obj), f"nonfinite number at {path}")

def norm(v): return math.sqrt(sum(i*i for i in v))

def frames(obj, where=""):
    if isinstance(obj,dict):
        if set(("position_m", "rotation_xyzw")).issubset(obj):
            n=norm(obj["rotation_xyzw"])
            require(abs(n-1)<1e-6,f"rotation quaternion not unit norm {where} ({n})")
        for k,v in obj.items(): frames(v, f"{where}/{k}")
    elif isinstance(obj,list):
        for i,v in enumerate(obj):frames(v,f"{where}/{i}")

def good_relative(s: str):
    p=Path(s)
    require(not p.is_absolute() and '..' not in p.parts and ':' not in s and '\\' not in s, f"unsafe relative path {s!r}")

def main():
    count=0
    loaded={}
    for kind,(schema_name,files) in MAPPING.items():
        require(files, f"no {kind} sample files")
        s=load(SCH / schema_name)
        jsonschema.Draft202012Validator.check_schema(s)
        validator=jsonschema.Draft202012Validator(s,format_checker=jsonschema.FormatChecker())
        for file in sorted(files):
            v=load(file)
            errs=list(validator.iter_errors(v))
            if errs:
                for err in errs:print(f"FAIL {file.relative_to(ROOT)}: {err.json_path}: {err.message}")
                return 1
            finite_floats(v,str(file))
            frames(v,str(file))
            loaded[str(file.relative_to(EX))]=v
            print("OK",file.relative_to(ROOT))
            count+=1
    component_by_id={x['id']:x for k,x in loaded.items() if k.startswith('components/')}
    for k,r in loaded.items():
        if not k.startswith('robots/'): continue
        links={x['id'] for x in r['links']}
        components={x['id']:x for x in r['components']}
        require(r['root_link_id'] in links, f"missing root link: {k}")
        require(len(links)==len(r['links']),f"duplicate links: {k}")
        require(len(components)==len(r['components']),f"duplicate instance IDs: {k}")
        for x in r['components']:
            require(x['catalogue_id'] in component_by_id, f"missing catalogue ID {x['catalogue_id']}")
            require(x['catalogue_version']==component_by_id[x['catalogue_id']]['version'], "component version mismatch")
            require(x['link_id'] in links,f"unattached component {x['id']}")
        require(len({j['id'] for j in r['joints']}) == len(r['joints']), f"duplicate joint IDs: {k}")
        require(len({x['id'] for x in r['electrical_connections']}) == len(r['electrical_connections']), f"duplicate connection IDs: {k}")
        require(len({x['id'] for x in r['controllers']}) == len(r['controllers']), f"duplicate MCU IDs: {k}")
        for j in r['joints']:
            require(j['parent_link_id'] in links and j['child_link_id'] in links, f"broken joint {j['id']}")
            require(abs(norm(j['axis_local'])-1)<1e-6,f"axis not normalized: {j['id']}")
            if j['actuator_component_id']:
                require(j['actuator_component_id'] in components,f"missing actuator {j['id']}")
        for conn in r['electrical_connections']:
            for edge in ('from','to'):
                instance,port=conn[edge].rsplit('.',1)
                require(instance in components,f"missing connection component {instance}")
                c=component_by_id[components[instance]['catalogue_id']]
                matches=[x for x in c['electrical_ports'] if x['id']==port]
                require(matches,f"missing port {conn[edge]}")
                require(matches[0]['interface']==conn['interface'], f"port type mismatch {conn[edge]} ({matches[0]['interface']} != {conn['interface']})")
        for ctl in r['controllers']:
            require(ctl['component_instance_id'] in components,f"missing controller {ctl['id']}")
            good_relative(ctl['firmware']['relative_path'])
        # Link graph must be connected to the single root and acyclic.
        adj={l:set() for l in links}
        for j in r['joints']:adj[j['parent_link_id']].add(j['child_link_id'])
        visited=set(); stack=set()
        def visit(n):
            require(n not in stack,f"mechanical cycle at {n}")
            if n in visited:return
            stack.add(n)
            for nxt in adj[n]:visit(nxt)
            stack.remove(n);visited.add(n)
        visit(r['root_link_id'])
        require(visited==links,f"unreachable links {links-visited}")
    for k,p in loaded.items():
        if k!='project.json':continue
        for path in [p['world_file'],*p['robot_files'],*p['component_files']]:
            good_relative(path)
            require((EX/path).is_file(),f"missing referenced project asset {path}")
    print(f"PASS: {count} sample JSON documents validated with schemas and basic semantic checks")
    return 0

if __name__=='__main__':
    try:sys.exit(main())
    except Exception as e:
        print(f"FAIL: {e}",file=sys.stderr)
        sys.exit(1)
