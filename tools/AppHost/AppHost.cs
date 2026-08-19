using AppHost;

var builder = DistributedApplication.CreateBuilder(args);

//var photino = builder.AddProject<SharpIDE_Photino>("photino");

builder.AddGodot("../../src/SharpIDE.Godot/SharpIDE.Godot.csproj", "sharpide-godot")
	.WithTerminal() // So the godot window is not suppressed
	.WithOtlpExporter();

var appHost = builder.Build();

await appHost.RunAsync();
