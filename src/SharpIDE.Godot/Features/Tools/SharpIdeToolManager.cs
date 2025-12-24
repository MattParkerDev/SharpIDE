using Godot;

namespace SharpIDE.Godot.Features.Tools;

public sealed class SharpIdeToolManager
{
    private readonly Dictionary<IdeToolId, IdeToolInstance> _instances = [];

    public IdeToolInstance GetInstance(IdeToolId id)
    {
        if (_instances.TryGetValue(id, out var instance))
        {
            return instance;
        }

        var descriptor = IdeToolDescriptors.Descriptors[id];
        instance = new IdeToolInstance(id, descriptor.Scene.Instantiate<Control>(), descriptor.Icon);
        _instances[id] = instance;

        return instance;
    }
}