using System.Collections.Specialized;
using Godot;
using ObservableCollections;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;

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
    extension(TreeItem treeItem)
    {
        public T? GetTypedMetadata<T>(int column) where T : RefCounted?
        {
            var metadata = treeItem.GetMetadata(column);
            var refCountedMetadata = metadata.As<RefCounted?>();
            if (refCountedMetadata is T correctTypeContainer)
            {
                return correctTypeContainer;
            }
            return null;
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
        public void RemoveChildAndQueueFree(Node child)
        {
            node.RemoveChild(child);
            child.QueueFree();
        }

        public void RemoveChildren()
        {
            foreach (var child in node.GetChildren())
            {
                node.RemoveChild(child);
            }
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