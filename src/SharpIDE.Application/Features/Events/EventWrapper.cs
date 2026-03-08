namespace SharpIDE.Application.Features.Events;

public class EventWrapper<TReturn>(Func<TReturn> @event) : EventWrapperBase<Func<TReturn>>(@event) where TReturn : Task
{
	public void InvokeParallelFireAndForget() => FireAndForget(() => InvokeParallelAsync());

	public async Task InvokeParallelAsync()
	{
		await InvokeDelegatesAsync(Event.GetInvocationList(), del => ((Func<TReturn>)del)());
	}
}

public class EventWrapper<TArg, TReturn>(Func<TArg, TReturn> @event) : EventWrapperBase<Func<TArg, TReturn>>(@event) where TReturn : Task
{
	public void InvokeParallelFireAndForget(TArg arg) => FireAndForget(() => InvokeParallelAsync(arg));
	public async Task InvokeParallelAsync(TArg arg)
	{
		await InvokeDelegatesAsync(Event.GetInvocationList(), del => ((Func<TArg, TReturn>)del)(arg));
	}
}

public class EventWrapper<TArg1, TArg2, TReturn>(Func<TArg1, TArg2, TReturn> @event) : EventWrapperBase<Func<TArg1, TArg2, TReturn>>(@event) where TReturn : Task
{
	public void InvokeParallelFireAndForget(TArg1 arg1, TArg2 arg2) => FireAndForget(() => InvokeParallelAsync(arg1, arg2));
	public async Task InvokeParallelAsync(TArg1 arg, TArg2 arg2)
	{
		await InvokeDelegatesAsync(Event.GetInvocationList(), del => ((Func<TArg1, TArg2, TReturn>)del)(arg, arg2));
	}
}

public class EventWrapper<TArg1, TArg2, TArg3, TReturn>(Func<TArg1, TArg2, TArg3, TReturn> @event) : EventWrapperBase<Func<TArg1, TArg2, TArg3, TReturn>>(@event) where TReturn : Task
{
	public void InvokeParallelFireAndForget(TArg1 arg1, TArg2 arg2, TArg3 arg3) => FireAndForget(() => InvokeParallelAsync(arg1, arg2, arg3));
	public async Task InvokeParallelAsync(TArg1 arg, TArg2 arg2, TArg3 arg3)
	{
		await InvokeDelegatesAsync(Event.GetInvocationList(), del => ((Func<TArg1, TArg2, TArg3, TReturn>)del)(arg, arg2, arg3));
	}
}
