using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.SymbolLookup;

public partial class SymbolLookupPopup : PopupPanel
{
	private Label _symbolNameLabel = null!;
	private VBoxContainer _usagesContainer = null!;

	public ImmutableArray<RoslynAnalysis.IdeReferenceLocationResult> IdeReferenceLocationResults { get; set; }
	public ISymbol Symbol { get; set; } = null!;
	private readonly PackedScene _symbolUsageScene = ResourceLoader.Load<PackedScene>("uid://dokm0dyac2enh");

	public override void _Ready()
	{
		_symbolNameLabel = GetNode<Label>("%SymbolNameLabel");
		_symbolNameLabel.Text = "";
		_usagesContainer = GetNode<VBoxContainer>("%UsagesVBoxContainer");
		_usagesContainer.GetChildren().ToList().ForEach(s => s.QueueFree());
		AboutToPopup += OnAboutToPopup;

		_usagesContainer.GetChildren().ToList().ForEach(s => s.QueueFree());
		foreach (var result in IdeReferenceLocationResults)
		{
			var resultNode = _symbolUsageScene.Instantiate<SymbolUsageComponent>();
			resultNode.Location = result.ReferenceLocation;
			resultNode.File = result.File;
			resultNode.EnclosingSymbol = result.EnclosingSymbol;
			resultNode.ParentSearchWindow = this;
			_usagesContainer.AddChild(resultNode);
		}
		_symbolNameLabel.Text = $"'{Symbol.Name}'";
	}

	private void OnAboutToPopup()
	{

	}
}
