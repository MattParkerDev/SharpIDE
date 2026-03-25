using Godot;

namespace SharpIDE.Godot.Features.CodeEditor;

/// reimplemented FindReplaceBar from godot's code_editor.cpp
public partial class FindReplaceBar : HBoxContainer
{
	private enum SearchMode { Current, Next, Prev }

	private Button _toggleReplaceButton = null!;
	private LineEdit _searchText = null!;
	private Label _matchesLabel = null!;
	private Button _findPrev = null!;
	private Button _findNext = null!;
	private CheckBox _caseSensitive = null!;
	private CheckBox _wholeWords = null!;
	private Button _hideButton = null!;

	private LineEdit _replaceText = null!;
	private Button _replace = null!;
	private Button _replaceAll = null!;
	private CheckBox _selectionOnly = null!;

	private HBoxContainer _hbcButtonReplace = null!;
	private HBoxContainer _hbcOptionReplace = null!;

	private CodeEdit? _textEditor;

	private uint _flags;
	private int _resultLine;
	private int _resultCol;
	private int _resultsCount = -1;
	private int _resultsCountToCurrent = -1;

	private bool _replaceAllMode;
	private bool _preserveCursor;

	/// <summary>Set to true when the results count needs a full recount.</summary>
	public bool NeedsToCountResults = true;

	/// <summary>Set to true by the code editor when the caret moved due to a search result navigation.</summary>
	public bool LineColChangedForResult;
	
	public override void _Ready()
	{
		BuildLayout();
	}

	private void BuildLayout()
	{
		// Toggle-replace arrow button (leftmost)
		_toggleReplaceButton = new Button();
		_toggleReplaceButton.ThemeTypeVariation = "FlatButton";
		_toggleReplaceButton.FocusMode = FocusModeEnum.Accessibility;
		_toggleReplaceButton.TooltipText = "Show Replace";
		_toggleReplaceButton.Pressed += OnToggleReplacePressed;
		AddChild(_toggleReplaceButton);

		// Center column: search + replace line edits stacked vertically
		var vbcLineEdit = new VBoxContainer();
		vbcLineEdit.Alignment = BoxContainer.AlignmentMode.Center;
		vbcLineEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		AddChild(vbcLineEdit);

		// Second column: action buttons (find prev/next, replace, replace all)
		var vbcButton = new VBoxContainer();
		AddChild(vbcButton);

		// Third column: option checkboxes (case, whole word, selection)
		var vbcOption = new VBoxContainer();
		AddChild(vbcOption);

		// --- Search row ---
		var hbcButtonSearch = new HBoxContainer();
		hbcButtonSearch.SizeFlagsVertical = SizeFlags.ExpandFill;
		hbcButtonSearch.Alignment = BoxContainer.AlignmentMode.End;
		vbcButton.AddChild(hbcButtonSearch);

		var hbcOptionSearch = new HBoxContainer();
		vbcOption.AddChild(hbcOptionSearch);

		// --- Replace row ---
		_hbcButtonReplace = new HBoxContainer();
		_hbcButtonReplace.SizeFlagsVertical = SizeFlags.ExpandFill;
		_hbcButtonReplace.Alignment = BoxContainer.AlignmentMode.End;
		vbcButton.AddChild(_hbcButtonReplace);

		_hbcOptionReplace = new HBoxContainer();
		vbcOption.AddChild(_hbcOptionReplace);

		// Search line edit
		_searchText = new LineEdit();
		_searchText.PlaceholderText = "Find";
		_searchText.TooltipText = "Find";
		_searchText.CustomMinimumSize = new Vector2(150, 0);
		_searchText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_searchText.TextChanged += OnSearchTextChanged;
		_searchText.TextSubmitted += OnSearchTextSubmitted;
		_searchText.KeepEditingOnTextSubmit = true;
		vbcLineEdit.AddChild(_searchText);

		// Matches label
		_matchesLabel = new Label();
		_matchesLabel.FocusMode = FocusModeEnum.Accessibility;
		_matchesLabel.Hide();
		hbcButtonSearch.AddChild(_matchesLabel);

		// Find prev button
		_findPrev = new Button();
		_findPrev.ThemeTypeVariation = "FlatButton";
		_findPrev.Text = "▲";
		_findPrev.Disabled = true;
		_findPrev.TooltipText = "Previous Match";
		_findPrev.FocusMode = FocusModeEnum.Accessibility;
		_findPrev.Pressed += () => SearchPrev();
		hbcButtonSearch.AddChild(_findPrev);

		// Find next button
		_findNext = new Button();
		_findNext.ThemeTypeVariation = "FlatButton";
		_findNext.Text = "▼";
		_findNext.Disabled = true;
		_findNext.TooltipText = "Next Match";
		_findNext.FocusMode = FocusModeEnum.Accessibility;
		_findNext.Pressed += () => SearchNext();
		hbcButtonSearch.AddChild(_findNext);

		// Case sensitive checkbox
		_caseSensitive = new CheckBox();
		_caseSensitive.Text = "Match Case";
		_caseSensitive.FocusMode = FocusModeEnum.Accessibility;
		_caseSensitive.Toggled += OnSearchOptionsChanged;
		hbcOptionSearch.AddChild(_caseSensitive);

		// Whole words checkbox
		_wholeWords = new CheckBox();
		_wholeWords.Text = "Whole Words";
		_wholeWords.FocusMode = FocusModeEnum.Accessibility;
		_wholeWords.Toggled += OnSearchOptionsChanged;
		hbcOptionSearch.AddChild(_wholeWords);

		// Replace line edit
		_replaceText = new LineEdit();
		_replaceText.PlaceholderText = "Replace";
		_replaceText.TooltipText = "Replace";
		_replaceText.CustomMinimumSize = new Vector2(150, 0);
		_replaceText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_replaceText.TextSubmitted += OnReplaceTextSubmitted;
		_replaceText.KeepEditingOnTextSubmit = true;
		vbcLineEdit.AddChild(_replaceText);

		// Replace button
		_replace = new Button();
		_replace.Text = "Replace";
		_replace.Pressed += OnReplaceButtonPressed;
		_hbcButtonReplace.AddChild(_replace);

		// Replace all button
		_replaceAll = new Button();
		_replaceAll.Text = "Replace All";
		_replaceAll.Pressed += DoReplaceAll;
		_hbcButtonReplace.AddChild(_replaceAll);

		// Selection only checkbox
		_selectionOnly = new CheckBox();
		_selectionOnly.Text = "Selection Only";
		_selectionOnly.FocusMode = FocusModeEnum.Accessibility;
		_selectionOnly.Toggled += OnSearchOptionsChanged;
		_hbcOptionReplace.AddChild(_selectionOnly);

		// Hide button (rightmost)
		_hideButton = new Button();
		_hideButton.ThemeTypeVariation = "FlatButton";
		_hideButton.Text = "✕";
		_hideButton.TooltipText = "Hide";
		_hideButton.FocusMode = FocusModeEnum.Accessibility;
		_hideButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		_hideButton.Pressed += HideBar;
		AddChild(_hideButton);

		// Start with replace row hidden
		_replaceText.Hide();
		_hbcButtonReplace.Hide();
		_hbcOptionReplace.Hide();

		UpdateMatchesDisplay();
	}

	public override void _Input(InputEvent @event)
	{
		if (Visible && @event is InputEventKey { Pressed: true } key && key.IsAction(InputStringNames.Cancel, true))
		{
			var focusOwner = GetViewport().GuiGetFocusOwner();
			if (_textEditor is not null && (_textEditor.HasFocus() || (focusOwner is not null && IsAncestorOf(focusOwner))))
			{
				HideBar();
				AcceptEvent();
			}
		}
	}

	public void SetTextEdit(CodeEdit editor)
	{
		if (_textEditor == editor) return;

		if (_textEditor is not null)
		{
			_textEditor.SetSearchText(string.Empty);
			_textEditor.TextChanged -= OnEditorTextChanged;
		}

		_textEditor = editor;

		if (_textEditor is null) return;

		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;
		_textEditor.TextChanged += OnEditorTextChanged;
		OnEditorTextChanged();
	}

	public string GetSearchText() => _searchText.Text;
	public string GetReplaceText() => _replaceText.Text;

	public bool IsCaseSensitive() => _caseSensitive.ButtonPressed;
	public bool IsWholeWords() => _wholeWords.ButtonPressed;
	public bool IsSelectionOnly() => _selectionOnly.ButtonPressed;

	public void PopupSearch(bool showOnly = false)
	{
		_replaceText.Hide();
		_hbcButtonReplace.Hide();
		_hbcOptionReplace.Hide();
		_selectionOnly.ButtonPressed = false;
		UpdateToggleReplaceButton(false);
		ShowSearch(withReplace: false, showOnly: showOnly);
	}

	public void PopupReplace()
	{
		if (!_replaceText.IsVisibleInTree())
		{
			_replaceText.Show();
			_hbcButtonReplace.Show();
			_hbcOptionReplace.Show();
			UpdateToggleReplaceButton(true);
		}

		_selectionOnly.ButtonPressed = _textEditor is not null &&
			_textEditor.HasSelection() &&
			_textEditor.GetSelectionFromLine() < _textEditor.GetSelectionToLine();

		ShowSearch(withReplace: true, showOnly: false);
	}

	public bool SearchCurrent()
	{
		UpdateFlags(backwards: false);
		GetSearchFrom(out int line, out int col, SearchMode.Current);
		return Search(_flags, line, col);
	}

	public bool SearchNext()
	{
		if (IsSelectionOnly() && !_replaceAllMode) return false;
		if (!IsVisibleInTree()) PopupSearch(showOnly: true);

		if ((_flags & (uint)TextEdit.SearchFlags.Backwards) != 0)
			NeedsToCountResults = true;

		UpdateFlags(backwards: false);
		GetSearchFrom(out int line, out int col, SearchMode.Next);
		return Search(_flags, line, col);
	}

	public bool SearchPrev()
	{
		if (IsSelectionOnly() && !_replaceAllMode) return false;
		if (!IsVisibleInTree()) PopupSearch(showOnly: true);

		var text = GetSearchText();

		if ((_flags & (uint)TextEdit.SearchFlags.Backwards) == 0)
			NeedsToCountResults = true;

		UpdateFlags(backwards: true);
		GetSearchFrom(out int line, out int col, SearchMode.Prev);

		col -= text.Length;
		if (col < 0)
		{
			line -= 1;
			if (line < 0) line = _textEditor!.GetLineCount() - 1;
			col = _textEditor!.GetLine(line).Length;
		}

		return Search(_flags, line, col);
	}

	private void ShowSearch(bool withReplace, bool showOnly)
	{
		Show();
		if (showOnly) return;

		bool onOneLine = _textEditor is not null &&
			_textEditor.HasSelection() &&
			_textEditor.GetSelectionFromLine() == _textEditor.GetSelectionToLine();

		bool focusReplace = withReplace && onOneLine;

		if (focusReplace)
		{
			_searchText.Deselect();
			Callable.From(() => _replaceText.GrabFocus(true)).CallDeferred();
		}
		else
		{
			_replaceText.Deselect();
			Callable.From(() => _searchText.GrabFocus(true)).CallDeferred();
		}

		if (onOneLine && _textEditor is not null)
		{
			_searchText.Text = _textEditor.GetSelectedText();
			_resultLine = _textEditor.GetSelectionFromLine();
			_resultCol = _textEditor.GetSelectionFromColumn();
		}

		if (GetSearchText().Length > 0)
		{
			if (focusReplace)
			{
				_replaceText.SelectAll();
				_replaceText.CaretColumn = _replaceText.Text.Length;
			}
			else
			{
				_searchText.SelectAll();
				_searchText.CaretColumn = _searchText.Text.Length;
			}

			_preserveCursor = true;
			OnSearchTextChanged(GetSearchText());
			_preserveCursor = false;
		}
	}

	private void HideBar()
	{
		_textEditor?.GrabFocus(true);
		_textEditor?.SetSearchText(string.Empty);
		_resultLine = -1;
		_resultCol = -1;
		Hide();
	}

	private void UpdateToggleReplaceButton(bool replaceVisible)
	{
		_toggleReplaceButton.Text = replaceVisible ? "▼" : "▶";
		_toggleReplaceButton.TooltipText = replaceVisible ? "Hide Replace (Ctrl+H)" : "Show Replace (Ctrl+H)";
	}

	private void UpdateFlags(bool backwards)
	{
		_flags = 0;
		if (IsWholeWords()) _flags |= (uint)TextEdit.SearchFlags.WholeWords;
		if (IsCaseSensitive()) _flags |= (uint)TextEdit.SearchFlags.MatchCase;
		if (backwards) _flags |= (uint)TextEdit.SearchFlags.Backwards;
	}

	private bool Search(uint flags, int fromLine, int fromCol)
	{
		if (_textEditor is null) return false;

		if (!_preserveCursor)
			_textEditor.RemoveSecondaryCarets();

		string text = GetSearchText();
		var pos = _textEditor.Search(text, flags, fromLine, fromCol);

		if (pos.X != -1)
		{
			if (!_preserveCursor && !IsSelectionOnly())
			{
				_textEditor.UnfoldLine(pos.Y);
				_textEditor.Select(pos.Y, pos.X, pos.Y, pos.X + text.Length);
				_textEditor.CenterViewportToCaret();
				_textEditor.SetCodeHint(string.Empty);
				_textEditor.CancelCodeCompletion();

				LineColChangedForResult = true;
			}

			_textEditor.SetSearchText(text);
			_textEditor.SetSearchFlags(flags);

			_resultLine = pos.Y;
			_resultCol = pos.X;

			UpdateResultsCount();
		}
		else
		{
			_resultsCount = 0;
			_resultLine = -1;
			_resultCol = -1;
			_textEditor.SetSearchText(string.Empty);
			_textEditor.SetSearchFlags(flags);
		}

		UpdateMatchesDisplay();
		return pos.X != -1;
	}

	private void GetSearchFrom(out int line, out int col, SearchMode mode)
	{
		if (_textEditor is null) { line = 0; col = 0; return; }

		if (!_textEditor.HasSelection() || IsSelectionOnly())
		{
			line = _textEditor.GetCaretLine();
			col = _textEditor.GetCaretColumn();

			if (mode == SearchMode.Prev &&
				line == _resultLine &&
				col >= _resultCol &&
				col <= _resultCol + GetSearchText().Length)
			{
				col = _resultCol;
			}
			return;
		}

		if (mode == SearchMode.Next)
		{
			line = _textEditor.GetSelectionToLine();
			col = _textEditor.GetSelectionToColumn();
		}
		else
		{
			line = _textEditor.GetSelectionFromLine();
			col = _textEditor.GetSelectionFromColumn();
		}
	}

	private void UpdateResultsCount()
	{
		if (_textEditor is null) return;

		GetSearchFrom(out int caretLine, out int caretColumn, SearchMode.Current);
		bool matchSelected = caretLine == _resultLine &&
			caretColumn == _resultCol &&
			!IsSelectionOnly() &&
			_textEditor.HasSelection();

		if (matchSelected && !NeedsToCountResults && _resultLine != -1 && _resultsCountToCurrent > 0)
		{
			_resultsCountToCurrent += (_flags & (uint)TextEdit.SearchFlags.Backwards) != 0 ? -1 : 1;

			if (_resultsCountToCurrent > _resultsCount)
				_resultsCountToCurrent -= _resultsCount;
			else if (_resultsCountToCurrent <= 0)
				_resultsCountToCurrent = _resultsCount;

			return;
		}

		string searched = GetSearchText();
		if (searched.Length == 0) return;

		NeedsToCountResults = !matchSelected;

		_resultsCount = 0;
		_resultsCountToCurrent = 0;

		bool searchedStartIsSymbol = IsSymbol(searched[0]);
		bool searchedEndIsSymbol = IsSymbol(searched[^1]);

		for (int i = 0; i < _textEditor.GetLineCount(); i++)
		{
			string lineText = _textEditor.GetLine(i);
			int colPos = 0;

			while (true)
			{
				colPos = IsCaseSensitive()
					? lineText.IndexOf(searched, colPos, System.StringComparison.Ordinal)
					: lineText.IndexOf(searched, colPos, System.StringComparison.OrdinalIgnoreCase);

				if (colPos == -1) break;

				if (IsWholeWords())
				{
					if (!searchedStartIsSymbol && colPos > 0 && !IsSymbol(lineText[colPos - 1]))
					{ colPos += searched.Length; continue; }
					if (!searchedEndIsSymbol && colPos + searched.Length < lineText.Length && !IsSymbol(lineText[colPos + searched.Length]))
					{ colPos += searched.Length; continue; }
				}

				_resultsCount++;

				if (i <= _resultLine && colPos <= _resultCol)
					_resultsCountToCurrent = _resultsCount;

				if (i == _resultLine && colPos < _resultCol && colPos + searched.Length > _resultCol)
					colPos = _resultCol;

				colPos += searched.Length;
			}
		}

		if (!matchSelected)
		{
			if (caretLine != _resultLine || caretColumn != _resultCol)
				_resultsCountToCurrent -= 1;

			if (_resultsCountToCurrent == 0 &&
				(caretLine > _resultLine || (caretLine == _resultLine && caretColumn > _resultCol)))
			{
				_resultsCountToCurrent = _resultsCount;
			}
		}
	}

	private void UpdateMatchesDisplay()
	{
		if (_searchText.Text.Length == 0 || _resultsCount == -1)
		{
			_matchesLabel.Hide();
		}
		else
		{
			_matchesLabel.Show();

			var fontColor = _resultsCount > 0
				? GetThemeColor(ThemeStringNames.FontColor, ThemeVariationStringNames.Gray700Label)
				: new Color("fc776a");
			_matchesLabel.AddThemeColorOverride(ThemeStringNames.FontColor, fontColor);

			if (_resultsCount == 0)
				_matchesLabel.Text = "No match";
			else if (_resultsCountToCurrent == -1)
				_matchesLabel.Text = $"{_resultsCount} match{(_resultsCount != 1 ? "es" : "")}";
			else
				_matchesLabel.Text = $"{_resultsCountToCurrent} of {_resultsCount} match{(_resultsCount != 1 ? "es" : "")}";
		}

		_findPrev.Disabled = _resultsCount < 1;
		_findNext.Disabled = _resultsCount < 1;
		_replace.Disabled = _searchText.Text.Length == 0;
		_replaceAll.Disabled = _searchText.Text.Length == 0;
	}

	private void DoReplace()
	{
		if (_textEditor is null) return;

		_textEditor.BeginComplexOperation();
		_textEditor.RemoveSecondaryCarets();

		bool selectionEnabled = _textEditor.HasSelection();
		int selBeginLine = 0, selBeginCol = 0, selEndLine = 0, selEndCol = 0;

		if (selectionEnabled)
		{
			selBeginLine = _textEditor.GetSelectionFromLine();
			selBeginCol = _textEditor.GetSelectionFromColumn();
			selEndLine = _textEditor.GetSelectionToLine();
			selEndCol = _textEditor.GetSelectionToColumn();
		}

		string replText = GetReplaceText();
		int searchLen = GetSearchText().Length;

		if (selectionEnabled && IsSelectionOnly())
		{
			_textEditor.SetCaretLine(selBeginLine, false, true, -1, 0);
			_textEditor.SetCaretColumn(selBeginCol, true, 0);
		}

		if (SearchCurrent())
		{
			_textEditor.UnfoldLine(_resultLine);
			_textEditor.Select(_resultLine, _resultCol, _resultLine, _resultCol + searchLen);

			if (selectionEnabled && IsSelectionOnly())
			{
				bool beforeStart = _resultLine < selBeginLine || (_resultLine == selBeginLine && _resultCol < selBeginCol);
				bool afterEnd = _resultLine > selEndLine || (_resultLine == selEndLine && _resultCol + searchLen > selEndCol);

				if (!beforeStart && !afterEnd)
				{
					_textEditor.InsertTextAtCaret(replText);
					if (_resultLine == selEndLine)
						selEndCol += replText.Length - searchLen;
				}
			}
			else
			{
				_textEditor.InsertTextAtCaret(replText);
			}
		}

		_textEditor.EndComplexOperation();
		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;

		if (selectionEnabled && IsSelectionOnly())
			_textEditor.Select(selBeginLine, selBeginCol, selEndLine, selEndCol);
		else
			_textEditor.Deselect();
	}

	private void DoReplaceAll()
	{
		if (_textEditor is null) return;

		_textEditor.BeginComplexOperation();
		_textEditor.RemoveSecondaryCarets();
		_textEditor.TextChanged -= OnEditorTextChanged;

		int origCaretLine = _textEditor.GetCaretLine();
		int origCaretCol = _textEditor.GetCaretColumn();
		int prevMatchLine = -1, prevMatchCol = -1;

		bool selectionEnabled = _textEditor.HasSelection();
		if (!IsSelectionOnly())
		{
			_textEditor.Deselect();
			selectionEnabled = false;
		}
		else
		{
			_resultLine = -1;
			_resultCol = -1;
		}

		int selBeginLine = 0, selBeginCol = 0, selEndLine = 0, selEndCol = 0;
		if (selectionEnabled)
		{
			selBeginLine = _textEditor.GetSelectionFromLine();
			selBeginCol = _textEditor.GetSelectionFromColumn();
			selEndLine = _textEditor.GetSelectionToLine();
			selEndCol = _textEditor.GetSelectionToColumn();
		}

		double vsval = _textEditor.GetVScroll();
		string replText = GetReplaceText();
		int searchLen = GetSearchText().Length;
		int rc = 0;

		_replaceAllMode = true;

		if (selectionEnabled && IsSelectionOnly())
		{
			_textEditor.SetCaretLine(selBeginLine, false, true, -1, 0);
			_textEditor.SetCaretColumn(selBeginCol, true, 0);
		}
		else
		{
			_textEditor.SetCaretLine(0, false, true, -1, 0);
			_textEditor.SetCaretColumn(0, true, 0);
		}

		if (SearchCurrent())
		{
			do
			{
				// Break if we wrapped around
				if (_resultLine < prevMatchLine || (_resultLine == prevMatchLine && _resultCol < prevMatchCol))
					break;

				prevMatchLine = _resultLine;
				prevMatchCol = _resultCol + replText.Length;

				_textEditor.UnfoldLine(_resultLine);
				_textEditor.Select(_resultLine, _resultCol, _resultLine, _resultCol + searchLen);

				if (selectionEnabled)
				{
					bool beforeStart = _resultLine < selBeginLine || (_resultLine == selBeginLine && _resultCol < selBeginCol);
					bool afterEnd = _resultLine > selEndLine || (_resultLine == selEndLine && _resultCol + searchLen > selEndCol);
					if (beforeStart || afterEnd) break;

					_textEditor.InsertTextAtCaret(replText);
					if (_resultLine == selEndLine)
						selEndCol += replText.Length - searchLen;
				}
				else
				{
					_textEditor.InsertTextAtCaret(replText);
				}

				rc++;
			} while (SearchNext());
		}

		_textEditor.EndComplexOperation();
		_replaceAllMode = false;

		_textEditor.SetCaretLine(origCaretLine, false, true, 0, 0);
		_textEditor.SetCaretColumn(origCaretCol, true, 0);

		if (selectionEnabled)
			_textEditor.Select(selBeginLine, selBeginCol, selEndLine, selEndCol);

		_textEditor.SetVScroll(vsval);

		var color = rc > 0 ? GetThemeColor(ThemeStringNames.FontColor, ThemeVariationStringNames.Gray700Label) : new Color("fc776a");
		_matchesLabel.AddThemeColorOverride(ThemeStringNames.FontColor, color);
		_matchesLabel.Show();
		_matchesLabel.Text = $"{rc} replaced.";

		Callable.From(() => _textEditor.TextChanged += OnEditorTextChanged).CallDeferred();
		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;
	}

	private void OnEditorTextChanged()
	{
		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;
		if (IsVisibleInTree())
		{
			_preserveCursor = true;
			SearchCurrent();
			_preserveCursor = false;
		}
	}

	private void OnSearchOptionsChanged(bool _)
	{
		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;
		SearchCurrent();
	}

	private void OnSearchTextChanged(string text)
	{
		_resultsCount = -1;
		_resultsCountToCurrent = -1;
		NeedsToCountResults = true;
		SearchCurrent();
	}

	private void OnSearchTextSubmitted(string text)
	{
		if (Input.IsKeyPressed(Key.Shift))
			SearchPrev();
		else
			SearchNext();
	}

	private void OnReplaceTextSubmitted(string text)
	{
		if (IsSelectionOnly() && _textEditor is not null && _textEditor.HasSelection())
		{
			DoReplaceAll();
			HideBar();
		}
		else if (Input.IsKeyPressed(Key.Shift))
		{
			DoReplace();
			SearchPrev();
		}
		else
		{
			DoReplace();
			SearchNext();
		}
	}

	private void OnReplaceButtonPressed()
	{
		DoReplace();
		SearchNext();
	}

	private void OnToggleReplacePressed()
	{
		bool replaceVisible = _replaceText.IsVisibleInTree();
		if (replaceVisible) PopupSearch(showOnly: true);
		else PopupReplace();
	}

	/// <summary>Returns true if the character is NOT a word character (letter, digit, or underscore).</summary>
	private static bool IsSymbol(char c) => !char.IsLetterOrDigit(c) && c != '_';
}
