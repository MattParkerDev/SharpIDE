using Godot;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.Run;

namespace SharpIDE.Godot;

[AttributeUsage(AttributeTargets.Field)]
public class InjectAttribute : Attribute;

public partial class DiAutoload : Node
{
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _currentScope;

    public override void _EnterTree()
    {
        GD.Print("[Injector] _EnterTree called");
        var services = new ServiceCollection();
        // Register services here
        services.AddScoped<BuildService>();
        services.AddScoped<RunService>();
        services.AddScoped<IdeFileExternalChangeHandler>();
        services.AddScoped<IdeCodeActionService>();
        services.AddScoped<IdeCompletionService>();
        services.AddScoped<FileChangedService>();
        services.AddScoped<IdeFileWatcher>();
        services.AddScoped<IdeOpenTabsFileManager>();
        services.AddScoped<RoslynAnalysis>();
        services.AddScoped<IdeFileOperationsService>();
        services.AddScoped<SharpIdeSolutionModificationService>();

        _serviceProvider = services.BuildServiceProvider();
        GetTree().NodeAdded += OnNodeAdded;
        GD.Print("[Injector] Service provider built and NodeAdded event subscribed");
    }

    public override void _Ready()
    {
        
    }

    /// The solution has changed, so reset the scope to get new services
    public void ResetScope()
    {
        _currentScope?.Dispose();
        _currentScope = _serviceProvider!.CreateScope();
    }

    private void OnNodeAdded(Node node)
    {
        // Inject dependencies into every new node
        InjectDependencies(node);
    }

    private void InjectDependencies(object target)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(flags))
        {
            if (Attribute.IsDefined(field, typeof(InjectAttribute)))
            {
                if (_currentScope is null)
                {
                    GD.PrintErr("[Injector] _currentScope was null when attempting to resolve service");
                    GetTree().Quit();
                    return;
                }
                var service = _currentScope!.ServiceProvider.GetService(field.FieldType);
                if (service is null)
                {
                    GD.PrintErr($"[Injector] No service registered for {field.FieldType}");
                    GetTree().Quit();
                    return;
                }

                field.SetValue(target, service);
            }
        }
    }
}