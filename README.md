<h1 align="center">
  <img src="src/SharpIDE.Godot/Resources/SharpIdeV3.svg" alt="SharpIDE icon" width="100" height="100" />
  <br>
  SharpIDE
</h1>

A modern, cross platform & open source IDE for .NET

### Building
See [CONTRIBUTING.md](CONTRIBUTING.md) for instructions for building and running SharpIDE locally!

### WIP
SharpIDE is a WIP, and contributions are always welcome! If you encounter an error, please raise an issue! 😊

### FAQs

<details><summary>SharpIDE won't open on MacOS Tahoe</summary>
Tahoe further restricted users' ability to run unsigned software. Follow these steps to get it running:

1. download a fresh zip of the latest release
2. extract
3. move SharpIDE.app to `Applications`
4. open terminal
5. run `xattr -d -r com.apple.quarantine /Applications/SharpIDE.app`
6. run `sudo codesign --force --deep --sign - /Applications/SharpIDE.app`

if codesign is not installed, install it with `xcode-select --install`
</details>

<img width="1600" height="900" alt="SharpIDE overview with the Problems pane open" src="docs/images/readme/overview.png" />

## Completions
<img width="1600" height="900" alt="Code completions" src="docs/images/readme/completions.png" />

## Signature Help
<img width="1600" height="900" alt="Method signature help" src="docs/images/readme/signature-help.png" />

## Code Action/Refactoring
<img width="1600" height="900" alt="Code actions and refactorings menu" src="docs/images/readme/code-action.png" />

## Symbol Info
<img width="1600" height="900" alt="Symbol info tooltip" src="docs/images/readme/symbol-info.png" />

## Razor Syntax Highlighting
<img width="1600" height="900" alt="Razor syntax highlighting" src="docs/images/readme/razor.png" />

## Run
<img width="1600" height="900" alt="Run pane with a running project" src="docs/images/readme/run.png" />

## Debug
<img width="1600" height="900" alt="Debugger stopped at a breakpoint" src="docs/images/readme/debug.png" />


## Build
<img width="1600" height="900" alt="Build pane" src="docs/images/readme/build.png" />

## NuGet (WIP)
<img width="1600" height="900" alt="NuGet package manager" src="docs/images/readme/nuget.png" />

## Test Explorer (WIP)
<img width="1600" height="900" alt="Test Explorer with test results" src="docs/images/readme/test-explorer.png" />

## Solution Picker
<img width="842" height="501" alt="Solution picker" src="docs/images/readme/solution-picker.png" />
