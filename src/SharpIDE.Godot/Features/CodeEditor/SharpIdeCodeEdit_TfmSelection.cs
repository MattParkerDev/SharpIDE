using Roslyn.Utilities;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private void OnTargetFrameworkSelected(long index)
	{
		if (_project is null || index < 0) return;
		var tfmSpecificLoadResults = _project.TfmSpecificLoadResults.Value
			.Where(result => result is { LoadState: MsBuildProjectLoadState.Loaded, Project: not null })
			.ToList();
		if (index >= tfmSpecificLoadResults.Count) return;

		_project.ActiveMsBuildProjectLoadResult.Value = tfmSpecificLoadResults[(int)index];
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
	}

	[RequiresGodotUiThread]
	private void UpdateTargetFrameworkOptions()
	{
		if (_project is null) return;
		var loadResults = _project.TfmSpecificLoadResults.Value
			.Where(result => result is { LoadState: MsBuildProjectLoadState.Loaded, Project: not null })
			.ToList();

		_tfmOptionButton.Clear();
		if (loadResults.Count <= 1)
		{
			_tfmOptionButton.Hide();
			return;
		}

		foreach (var loadResult in loadResults)
		{
			_tfmOptionButton.AddItem(loadResult.Project!.GetPropertyValue("TargetFramework"));
		}

		var selectedIndex = loadResults.IndexOf(_project.ActiveMsBuildProjectLoadResult.Value);
		_tfmOptionButton.Select(selectedIndex >= 0 ? selectedIndex : 0);
		_tfmOptionButton.Show();
	}

	[RequiresGodotUiThread]
	private void UpdateSelectedTargetFramework()
	{
		if (_project is null) return;
		var selectedIndex = _project.TfmSpecificLoadResults.Value
			.Where(result => result is { LoadState: MsBuildProjectLoadState.Loaded, Project: not null })
			.IndexOf(_project.ActiveMsBuildProjectLoadResult.Value);
		if (selectedIndex >= 0) _tfmOptionButton.Select(selectedIndex);
	}
}
