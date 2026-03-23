## Aspire AppHost

Running SharpIDE via the Aspire AppHost allows you to inspect the OTEL telemetry and logs

### Setup
1. Download the latest godot (.NET) binary, and put it somewhere (e.g. Documents/Godot/Path)
2. Rename e.g. Godot_v4.6.1-stable_mono_win64.exe to godot.exe
3. Put the folder containing the godot executable on your PATH, and ensure it resolves from a shell
4. Done - Aspire will launch godot via the executable on the PATH

TODO: Create better godot integration with aspire/wait for libgodot.
