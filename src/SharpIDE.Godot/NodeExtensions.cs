using System.Runtime.CompilerServices;
using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

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

    private static readonly ConditionalWeakTable<TreeItem, ISharpIdeNode> TreeItemSharpIdeNode = [];
    extension(TreeItem treeItem)
    {
        public ISharpIdeNode? SharpIdeNode
        {
            get => TreeItemSharpIdeNode.TryGetValue(treeItem, out var s) ? s : null;
            set
            {
                if (value is null)
                {
                    TreeItemSharpIdeNode.Remove(treeItem);
                }
                else
                {
                    TreeItemSharpIdeNode.AddOrUpdate(treeItem, value);
                }
            }
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
        public Task<T> InvokeAsync<T>(Func<T> workItem)
        {
            var taskCompletionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.SynchronizationContext.Post(_ =>
            {
                try
                {
                    var result = workItem();
                    taskCompletionSource.SetResult(result);
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }, null);
            return taskCompletionSource.Task;
        }
        public Task InvokeAsync(Action workItem)
        {
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            //WorkerThreadPool.AddTask();
            Dispatcher.SynchronizationContext.Post(_ =>
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
            }, null);
            return taskCompletionSource.Task;
        }
        
        public Task InvokeAsync(Func<Task> workItem)
        {
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.SynchronizationContext.Post(async void (_) =>
            {
                try
                {
                    await workItem();
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }, null);
            return taskCompletionSource.Task;
        }
        
        public Task InvokeDeferredAsync(Action workItem)
        {
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
        
        public Task InvokeDeferredAsync(Func<Task> workItem)
        {
            var taskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            //WorkerThreadPool.AddTask();
            Callable.From(async void () =>
            {
                try
                {
                    await workItem();
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
}

public static class GodotTask
{
    extension<T>(Task<T> task)
    {
        public Task AsTask() => task;
    }

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