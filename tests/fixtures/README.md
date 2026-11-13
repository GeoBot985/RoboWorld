# Test fixtures

The controller-profile JSON files are small inputs used to verify profile identity and runtime preflight behavior. They retain the measured model values and original identity records from the characterization results. The tests keep the unqualified label and exercise fail-closed profile loading. A qualified historical profile is also checked against the current local firmware build; byte differences correctly invalidate the old identity. Build sample firmware using the instructions under `samples/firmware/stm32-drive/` before running the .NET integration suites.
