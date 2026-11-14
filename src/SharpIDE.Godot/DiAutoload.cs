using Godot;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpIDE.Application;
using SharpIDE.Godot.Features.ActivityListener;

namespace SharpIDE.Godot;

[AttributeUsage(AttributeTargets.Field)]
public class InjectAttribute : Attribute;

public partial class DiAutoload : Node
{
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _currentScope;
    private IServiceScope? _nextScope;
    private readonly Lock _scopeLock = new();

    public override void _EnterTree()
    {
        GD.Print("[Injector] _EnterTree called");
        var services = new ServiceCollection();

        services.AddApplication();
        services.AddScoped<SharpIdeSolutionAccessor>();
        services.AddScoped<ActivityMonitor>();

        services.AddHttpClient();
        services.AddLogging(builder =>
        {
            //builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddConsole();
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });
        });
        services.AddGodotOpenTelemetry();

        _serviceProvider = services.BuildServiceProvider();
        GetTree().NodeAdded += OnNodeAdded;
        GD.Print("[Injector] Service provider built and NodeAdded event subscribed");
    }

    public override void _Ready()
    {
        
    }

    /// Creates the scope for the solution being opened, ahead of ResetScope, so its services can be used (e.g. to start its build hosts) before the IdeRoot is added to the tree.
    /// The current scope (the previous solution's) stays alive until ResetScope. Thread safe.
    public IServiceProvider CreateNextScope()
    {
        lock (_scopeLock)
        {
            _nextScope?.Dispose();
            _nextScope = _serviceProvider!.CreateScope();
            return _nextScope.ServiceProvider;
        }
    }

    /// The solution has changed, so reset the scope to get new services - the one from CreateNextScope, if created
    public void ResetScope()
    {
        lock (_scopeLock)
        {
            _currentScope?.Dispose();
            _currentScope = _nextScope ?? _serviceProvider!.CreateScope();
            _nextScope = null;
        }
    }

    private void OnNodeAdded(Node node)
    {
        // Inject dependencies into every new node
        InjectDependencies(node);
    }

    /// Each node type's [Inject] fields. NodeAdded fires on the UI thread for every node added to the tree - hundreds at once when e.g. a list is filled - and most
    /// have none, so the reflection is done once per type rather than allocating the field array and checking each field's attributes every time
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> InjectableFieldsByType = new();

    private static FieldInfo[] GetInjectableFields(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return InjectableFieldsByType.GetOrAdd(type, static t => t.GetFields(flags).Where(field => Attribute.IsDefined(field, typeof(InjectAttribute))).ToArray());
    }

    private void InjectDependencies(object target)
    {
        foreach (var field in GetInjectableFields(target.GetType()))
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