using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

internal sealed class DebuggerIntelliSenseWorkspace : Workspace
{
	public DebuggerIntelliSenseWorkspace(Solution solution) : base(solution.Workspace.Services.HostServices, WorkspaceKind.Debugger)
	{
		SetCurrentSolutionEx(solution);
	}

	//public void OpenDocument(DocumentId documentId, SourceTextContainer textContainer) => OnDocumentOpened(documentId, textContainer);
}
