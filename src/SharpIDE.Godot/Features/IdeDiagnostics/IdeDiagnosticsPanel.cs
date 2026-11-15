using Godot;

namespace SharpIDE.Godot.Features.IdeDiagnostics;

public partial class IdeDiagnosticsPanel : Control
{
	private GraphEdit _graphEdit = null!;
	public override void _Ready()
	{
		_graphEdit = GetNode<GraphEdit>("%GraphEdit");
		//_graphEdit.ConnectionRequest += GraphEditOnConnectionRequest;
		//var graphNode = GetNode<Node>("%GraphNode");

	}

	// private void GraphEditOnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort)
	// {
	//     GD.Print($"Connection requested from {fromNode} port {fromPort} to {toNode} port {toPort}");
	//     _graphEdit.ConnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
	// }
}
