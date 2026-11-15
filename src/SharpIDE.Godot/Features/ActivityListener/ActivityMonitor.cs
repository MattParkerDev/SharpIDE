using System.Diagnostics;
using SharpIDE.Application;
using SharpIDE.Application.Features.Events;

namespace SharpIDE.Godot.Features.ActivityListener;

public class ActivityMonitor
{
	public EventWrapper<Activity, Task> ActivityChanged { get; } = new(_ => Task.CompletedTask);

	public ActivityMonitor()
	{
		var listener = new System.Diagnostics.ActivityListener
		{
			ShouldListenTo = source => source == SharpIdeOtel.Source,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
			ActivityStarted = activity => ActivityChanged.InvokeParallelFireAndForget(activity),
			ActivityStopped = activity => ActivityChanged.InvokeParallelFireAndForget(activity),
		};

		ActivitySource.AddActivityListener(listener);
	}
}
