using R3;

namespace SharpIDE.Application.Features.Events;

public abstract class EventWrapperBase<TDelegate>(TDelegate @event) where TDelegate : Delegate
{
	protected TDelegate Event = @event;

	public IDisposable Subscribe(TDelegate handler)
	{
		Event = (TDelegate) Delegate.Combine(Event, handler);

		return Disposable.Create(() => Unsubscribe(handler));
	}

	public void Unsubscribe(TDelegate handler) => Event = (TDelegate)Delegate.Remove(Event, handler)!;

	protected static async void FireAndForget(Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"An exception occurred in an event handler: {ex}");
		}
	}

	protected static async Task InvokeDelegatesAsync(IEnumerable<Delegate> invocationList, Func<Delegate, Task> delegateExecutorDelegate)
	{
		var tasks = invocationList.Select(async del =>
		{
			try
			{
				await delegateExecutorDelegate(del).ConfigureAwait(false);
				return null;
			}
			catch (Exception ex)
			{
				return ex;
			}
		});

		var results = await Task.WhenAll(tasks).ConfigureAwait(false);
		var exceptions = results.Where(r => r is not null).Select(r => r!).ToList();
		if (exceptions.Count != 0)
		{
			throw new AggregateException(exceptions);
		}
	}
}
