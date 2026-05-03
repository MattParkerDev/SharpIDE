using System.Buffers;
using System.Runtime.CompilerServices;
using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot;

public static class ControlExtensions
{
    // extension(Control control)
    // {
    //     public void BindChildren(ObservableHashSet<SharpIdeProjectModel> list, PackedScene scene)
    //     {
    //         var view = list.CreateView(x =>
    //         {
    //             var node = scene.Instantiate<ProblemsPanelProjectEntry>();
    //             node.Project = x;
    //             Callable.From(() => control.AddChild(node)).CallDeferred();
    //             return node;
    //         });
    //         view.ViewChanged += OnViewChanged;
    //     }
    //     private static void OnViewChanged(in SynchronizedViewChangedEventArgs<SharpIdeProjectModel, ProblemsPanelProjectEntry> eventArgs)
    //     {
    //         GD.Print("View changed: " + eventArgs.Action);
    //         if (eventArgs.Action == NotifyCollectionChangedAction.Remove)
    //         {
    //             eventArgs.OldItem.View.QueueFree();
    //         }
    //     }
    // }
}

/// Has no functionality, just used as a reminder to indicate that a method must be called on the Godot UI thread.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequiresGodotUiThreadAttribute : Attribute
{
}

public static class NodeExtensions
{
    extension(RenderingServerInstance renderingServerInstance)
    {
        // https://github.com/godotengine/godot/blob/a4bbad2ba8a8ecd4e756e49de5c83666f12a9bd5/scene/main/canvas_item.cpp#L717
        public void DrawDashedLine(Rid canvasItemRid, Vector2 from, Vector2 to, Color color, float width = -1.0f, float dash = 2.0f, bool aligned = true, bool antialiased = false)
        {
            if (dash <= 0.0f)
            {
                GD.PushError("draw_dashed_line: dash length must be greater than 0");
                return;
            }

            var length = (to - from).Length();
            var step = dash * (to - from).Normalized();

            if (length < dash || step == Vector2.Zero)
            {
                renderingServerInstance.CanvasItemAddLine(canvasItemRid, from, to, color, width, antialiased);
                return;
            }

            int steps = aligned ? Mathf.CeilToInt(length / dash) : Mathf.FloorToInt(length / dash);
            if (steps % 2 == 0)
            {
                steps--;
            }

            var off = from;
            if (aligned)
            {
                off += (to - from).Normalized() * (length - steps * dash) / 2.0f;
            }

            //Span<Vector2> points = steps <= 128 ? stackalloc Vector2[steps + 1] : new Vector2[steps + 1];
            Span<Vector2> points = stackalloc Vector2[steps + 1];
            for (var i = 0; i < steps; i += 2)
            {
                points[i] = (i == 0) ? from : off;
                points[i + 1] = (aligned && i == steps - 1) ? to : (off + step);
                off += step * 2;
            }

            ReadOnlySpan<Color> colors = stackalloc Color[1] { color };

            renderingServerInstance.CanvasItemAddMultiline(canvasItemRid, points, colors, width, antialiased);
        }
    }

    extension(Font font)
    {
        public Vector2 GetStringsSize(
            ReadOnlySpan<string> strings,
            HorizontalAlignment alignment = HorizontalAlignment.Left,
            float width = -1f,
            int fontSize = 16 /*0x10*/,
            TextServer.JustificationFlag justificationFlags = TextServer.JustificationFlag.Kashida | TextServer.JustificationFlag.WordBound,
            TextServer.Direction direction = TextServer.Direction.Auto,
            TextServer.Orientation orientation = TextServer.Orientation.Horizontal)
        {
            var size = Vector2.Zero;
            foreach (var str in strings)
            {
                var strSize = font.GetStringSize(str, alignment, width, fontSize, justificationFlags, direction, orientation);
                size.X += strSize.X;
                size.Y = Math.Max(size.Y, strSize.Y);
            }
            return size;
        }
    }

    extension(OptionButton optionButton)
    {
        public int? GetOptionIndexOrNullForString(string optionString)
        {
            for (var i = 0; i < optionButton.GetItemCount(); i++)
            {
                if (optionButton.GetItemText(i) == optionString)
                {
                    return i;
                }
            }
            return null;
        }
    }

    extension<TKey, TValue>(ConditionalWeakTable<TKey, TValue> conditionalWeakTable) where TKey : class where TValue : class
    {
        public void AddOrUpdateOrRemove(TKey key, TValue? value)
        {
            if (value is null)
            {
                conditionalWeakTable.Remove(key);
            }
            else
            {
                conditionalWeakTable.AddOrUpdate(key, value);
            }
        }
    }

    private static readonly ConditionalWeakTable<TreeItem, ISharpIdeNode> TreeItemSharpIdeNode = [];
    private static readonly ConditionalWeakTable<TreeItem, SharpIdeDiagnostic> TreeItemSharpIdeDiagnostic = [];
    private static readonly ConditionalWeakTable<TreeItem, IDisposable> TreeItemDisposable = [];
    extension(TreeItem treeItem)
    {
        public ISharpIdeNode? SharpIdeNode
        {
            get => TreeItemSharpIdeNode.TryGetValue(treeItem, out var s) ? s : null;
            set => TreeItemSharpIdeNode.AddOrUpdateOrRemove(treeItem, value);
        }
        public SharpIdeDiagnostic? SharpIdeDiagnostic
        {
            get => TreeItemSharpIdeDiagnostic.TryGetValue(treeItem, out var s) ? s : null;
            set => TreeItemSharpIdeDiagnostic.AddOrUpdateOrRemove(treeItem, value);
        }
        public IDisposable? SharpIdeDisposable
        {
            get => TreeItemDisposable.TryGetValue(treeItem, out var s) ? s : null;
            set => TreeItemDisposable.AddOrUpdateOrRemove(treeItem, value);
        }
        public void MoveToIndexInParent(int currentIndex, int newIndex)
        {
            var parent = treeItem.GetParent()!;
            if (newIndex == currentIndex) throw new ArgumentException("New index is the same as current index", nameof(newIndex));

            var target = parent.GetChild(newIndex);
            if (newIndex < currentIndex)
                treeItem.MoveBefore(target);
            else
                treeItem.MoveAfter(target);
        }
    }
    extension(Node node)
    {
        public void QueueFreeChildren()
        {
            foreach (var child in node.GetChildren())
            {
                child.QueueFree();
            }
        }
        public void RemoveAndQueueFreeChildren()
        {
            foreach (var child in node.GetChildren())
            {
                node.RemoveChild(child);
                child.QueueFree();
            }
        }
        public void RemoveChildAndQueueFree(Node child)
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
        public Task<T> InvokeAsync<T>(Func<T> workItem, [CallerMemberName] string? callerName = null)
        {
            GuardAgainstUiThreadCallingInvokeAsync(callerName);
            var taskCompletionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.SynchronizationContext.Post(static state =>
            {
                var (workItem, tcs) = ((Func<T>, TaskCompletionSource<T>))state!;
                try
                {
                    var result = workItem();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, (workItem, taskCompletionSource));
            return taskCompletionSource.Task;
        }
        public Task InvokeAsync(Action workItem, [CallerMemberName] string? callerName = null)
        {
            GuardAgainstUiThreadCallingInvokeAsync(callerName);
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            //WorkerThreadPool.AddTask();
            Dispatcher.SynchronizationContext.Post(static state =>
            {
                var (workItem, tcs) = ((Action, TaskCompletionSource))state!;
                try
                {
                    workItem();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, (workItem, taskCompletionSource));
            return taskCompletionSource.Task;
        }
        
        public Task InvokeAsync(Func<Task> workItem, [CallerMemberName] string? callerName = null)
        {
            GuardAgainstUiThreadCallingInvokeAsync(callerName);
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.SynchronizationContext.Post(static async void (state) =>
            {
                var (workItem, tcs) = ((Func<Task>, TaskCompletionSource))state!;
                try
                {
                    await workItem().ConfigureAwait(false);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, (workItem, taskCompletionSource));
            return taskCompletionSource.Task;
        }
        
        public Task InvokeDeferredAsync(Action workItem, [CallerMemberName] string? callerName = null)
        {
            GuardAgainstUiThreadCallingInvokeAsync(callerName);
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            //WorkerThreadPool.AddTask();
            Callable.From(() =>
            {
                try
                {
                    workItem();
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }).CallDeferred();
            return taskCompletionSource.Task;
        }
        
        public Task InvokeDeferredAsync(Func<Task> workItem, [CallerMemberName] string? callerName = null)
        {
            GuardAgainstUiThreadCallingInvokeAsync(callerName);
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            //WorkerThreadPool.AddTask();
            Callable.From(async void () =>
            {
                try
                {
                    await workItem().ConfigureAwait(false);
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }).CallDeferred();
            return taskCompletionSource.Task;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GuardAgainstUiThreadCallingInvokeAsync(string? callerName = null)
    {
#if DEBUG
        if (SynchronizationContext.Current == Dispatcher.SynchronizationContext)
        {
            GD.PrintErr($"{callerName} - InvokeAsync should not be called from the Godot UI thread. If you're still/already on the UI thread, just call the godot api you want to use directly.");
        }
#endif
    }
}

public static class GodotTask
{
    extension(Task task)
    {
        public static async Task GodotRun(Action action)
        {
            await Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"Error: {ex}");
                }
            });
        }
    
        public static async Task GodotRun(Func<Task> action)
        {
            await Task.Run(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"Error: {ex}");
                }
            });
        }
    }
}

public static class StreamExtensions
{
    public static Task CopyToWithProgressAsync(this Stream source, Stream destination, int bufferSize, IProgress<long>? progress = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source.CanRead is false) throw new ArgumentException("Source Stream must be readable", nameof(source));
        if (destination.CanWrite is false) throw new ArgumentException("Destination Stream must be writable", nameof(destination));
        
        return Core(source, destination, bufferSize, progress, cancellationToken);

        static async Task Core(Stream source, Stream destination, int bufferSize, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            long totalBytesRead = 0;
            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(new Memory<byte>(buffer), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await destination.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalBytesRead += bytesRead;
                    progress?.Report(totalBytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
