using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoGSaveManager
{
	/// Lookahead: plays every option of the current choice forward (and the options of the choices those lead to, up to
	/// <see cref="LookaheadDepth"/> choices deep) and shows what each one changes and where it jumps, as a tree above the source.
	public partial class ChoiceExplorerWindow
	{
		private class LookaheadTrace
		{
			public int choiceLine = -1;
			public string stop, error;
			public bool random;
			public int steps;
			public readonly List<string> jumps = new List<string>( 4 );

			public void Jump( string to )
			{
				if( jumps.Count == 0 || jumps[jumps.Count - 1] != to )
					jumps.Add( to );
			}
		}

		private class ChoiceOption
		{
			public string text, condition;
			public int line;
			public bool hidden, disabled;
		}

		private const int LookaheadStepLimit = 20000;
		private const int LookaheadBranchLimit = 400;
		private const int LookaheadMaxDepth = 5;
		private const string DimColor = "#808080";

		/// Non-null while a lookahead branch is being simulated by <see cref="ExtractChoicesInternal"/>.
		private static LookaheadTrace lookahead;

		private readonly List<Text> treeEntries = new List<Text>( 2 );
		private int lookaheadBranches;

		private int LookaheadDepth
		{
			get { return Mathf.Clamp( PlayerPrefs.GetInt( "LookaheadDepth", 2 ), 0, LookaheadMaxDepth ); }
			set
			{
				PlayerPrefs.SetInt( "LookaheadDepth", Mathf.Clamp( value, 0, LookaheadMaxDepth ) );
				PlayerPrefs.Save();
			}
		}

		private void UpdateLookaheadHotkeys()
		{
			if( string.IsNullOrEmpty( savePath ) )
				return;

			// Leave + and - to whatever input field has focus
			GameObject selected = ( EventSystem.current != null ) ? EventSystem.current.currentSelectedGameObject : null;
			if( selected != null && selected.GetComponent<InputField>() != null )
				return;

			int delta = 0;
			if( Input.GetKeyDown( KeyCode.Equals ) || Input.GetKeyDown( KeyCode.Plus ) || Input.GetKeyDown( KeyCode.KeypadPlus ) )
				delta = 1;
			else if( Input.GetKeyDown( KeyCode.Minus ) || Input.GetKeyDown( KeyCode.KeypadMinus ) )
				delta = -1;

			if( delta != 0 && LookaheadDepth + delta >= 0 && LookaheadDepth + delta <= LookaheadMaxDepth )
			{
				LookaheadDepth += delta;
				OnNewSaveOpened();
			}
		}

		/// Called right after <see cref="ExtractChoices"/>, while <see cref="scene"/> and <see cref="saveData"/> hold the state at the current choice.
		private string[] BuildLookahead()
		{
			List<string> result = new List<string>( 2 );
			int depth = LookaheadDepth;
			StringBuilder tree = new StringBuilder( 4096 );
			tree.Append( "<b>Lookahead: " ).Append( depth ).Append( " choice" ).Append( depth == 1 ? "" : "s" ).Append( " deep</b> <color=" ).Append( DimColor ).Append( ">(press + or - to change; 0 turns it off)</color>\n" );

			if( depth <= 0 || scene == null || topLineNumber < 0 || topLineNumber >= scene.Lines.Length )
			{
				result.Add( tree.ToString().TrimEnd( '\n' ) );
				return result.ToArray();
			}

			string command = GetCommandName( scene.Lines[topLineNumber] );
			if( command != "choice" && command != "fake_choice" )
			{
				result.Add( tree.Append( "<color=" ).Append( DimColor ).Append( ">No choice on this page.</color>" ).ToString() );
				return result.ToArray();
			}

			SceneData rootScene = scene;
			JSONNode rootData = saveData;
			bool rootMerged = mergedPreviousBooksStats;
			lookaheadBranches = 0;

			try
			{
				JSONNode state = rootData.Clone();
				MergePreviousBooksStats( state );
				mergedPreviousBooksStats = true; // Merged above, so that merging can't show up as a change

				using( FileStream fs = new FileStream( gameAsarFilePath, FileMode.Open, FileAccess.Read ) )
					AppendChoiceTree( fs, rootScene, topLineNumber, state, depth, 0, tree, result );
			}
			catch( Exception e )
			{
				tree.Append( "<b>Lookahead failed: " ).Append( e.Message ).Append( "</b>\n" );
			}
			finally
			{
				lookahead = null;
				scene = rootScene;
				saveData = rootData;
				mergedPreviousBooksStats = rootMerged;
				currentLine = lastErrorLine = null;
			}

			if( lookaheadBranches > LookaheadBranchLimit )
				tree.Append( "<color=" ).Append( DimColor ).Append( ">Stopped after " ).Append( LookaheadBranchLimit ).Append( " branches; press - for a shallower tree.</color>\n" );

			if( tree.Length > 0 )
				result.Add( tree.ToString().TrimEnd( '\n' ) );

			return result.ToArray();
		}

		private void AppendChoiceTree( FileStream fs, SceneData choiceScene, int choiceLine, JSONNode state, int depth, int level, StringBuilder tree, List<string> result )
		{
			scene = choiceScene;
			saveData = state;
			List<ChoiceOption> options = ParseChoiceOptions( choiceLine );
			string pad = new string( ' ', level * 6 ), detailPad = pad + "      ";
			List<string> scratch = new List<string>( 1 );

			foreach( ChoiceOption option in options )
			{
				if( tree.Length > MaxStringLength )
				{
					result.Add( tree.ToString().TrimEnd( '\n' ) );
					tree.Length = 0;
				}

				tree.Append( pad ).Append( "<color=" ).Append( ChoiceLineColor ).Append( "><b>#" ).Append( option.text ).Append( "</b></color>" );
				if( option.hidden || option.disabled )
				{
					tree.Append( " <color=" ).Append( DimColor ).Append( ">(" ).Append( option.hidden ? "hidden" : "unselectable" );
					if( !string.IsNullOrEmpty( option.condition ) )
						tree.Append( ": " ).Append( option.condition );

					tree.Append( ")</color>\n" );
					continue;
				}

				tree.Append( '\n' );
				if( ++lookaheadBranches > LookaheadBranchLimit )
					return;

				scene = choiceScene;
				saveData = state.Clone();
				saveData["lineNum"] = option.line + 1;
				lookahead = new LookaheadTrace();
				try
				{
					ExtractChoicesInternal( fs, scratch );
				}
				catch( Exception e )
				{
					lookahead.error = e.Message;
				}

				LookaheadTrace trace = lookahead;
				lookahead = null;
				SceneData nextScene = scene;
				JSONNode nextState = saveData;

				AppendChanges( state, nextState, choiceScene != nextScene, detailPad, tree );
				if( trace.jumps.Count > 0 )
					tree.Append( detailPad ).Append( "<color=" ).Append( OtherCommandLineColor ).Append( ">-> " ).Append( string.Join( " -> ", trace.jumps.ToArray() ) ).Append( "</color>\n" );
				if( trace.random )
					tree.Append( detailPad ).Append( "<color=" ).Append( DimColor ).Append( ">(includes a *rand; this is one possible roll)</color>\n" );
				if( trace.error != null )
					tree.Append( detailPad ).Append( "<color=" ).Append( VariableValueColor ).Append( ">(could not follow: " ).Append( trace.error ).Append( ")</color>\n" );

				if( trace.choiceLine >= 0 )
				{
					if( depth > 1 )
						AppendChoiceTree( fs, nextScene, trace.choiceLine, nextState, depth - 1, level + 1, tree, result );
					else
						tree.Append( detailPad ).Append( "<color=" ).Append( DimColor ).Append( ">then a choice at " ).Append( nextScene.Name ).Append( " line " ).Append( trace.choiceLine + 1 ).Append( "</color>\n" );
				}
				else if( trace.stop != null )
					tree.Append( detailPad ).Append( "<color=" ).Append( DimColor ).Append( ">then " ).Append( trace.stop ).Append( "</color>\n" );

				if( lookaheadBranches > LookaheadBranchLimit )
					return;

				scene = choiceScene;
				saveData = state;
			}
		}

		/// Lists the choice's options with whether each is shown and selectable, evaluating the *if blocks they sit in.
		private List<ChoiceOption> ParseChoiceOptions( int choiceLine )
		{
			List<ChoiceOption> options = new List<ChoiceOption>( 4 );
			int choiceIndentation = CalculateLineIndentation( choiceLine );

			// Open *if/*elseif/*else blocks inside the choice: their indentation and whether their branch is the one taken
			List<int> blockIndentations = new List<int>( 2 );
			List<bool> blockActive = new List<bool>( 2 );
			Dictionary<int, bool> chainTaken = new Dictionary<int, bool>();

			for( int i = choiceLine + 1; i < scene.Lines.Length; i++ )
			{
				if( IsLineWhitespace( i ) )
					continue;

				int indentation = CalculateLineIndentation( i );
				if( indentation <= choiceIndentation )
					break;

				while( blockIndentations.Count > 0 && blockIndentations[blockIndentations.Count - 1] >= indentation )
				{
					blockIndentations.RemoveAt( blockIndentations.Count - 1 );
					blockActive.RemoveAt( blockActive.Count - 1 );
				}

				bool enclosingActive = !blockActive.Contains( false );
				string line = OnCurrentLineChanged( scene.Lines[i].Trim() );
				if( IsLineChoiceOption( line ) )
				{
					options.Add( ParseChoiceOption( line, i, enclosingActive ) );

					// Skip the option's body
					while( i + 1 < scene.Lines.Length && ( IsLineWhitespace( i + 1 ) || CalculateLineIndentation( i + 1 ) > indentation ) )
						i++;

					continue;
				}

				string command = GetCommandName( line );
				if( command == "if" || command == "elseif" || command == "elsif" || command == "else" )
				{
					bool taken;
					if( command == "if" || !chainTaken.TryGetValue( indentation, out taken ) )
						taken = false;

					bool active = false;
					if( enclosingActive && !taken )
					{
						if( command == "else" )
							active = true;
						else
						{
							try { active = EvaluateCondition( line, i, command.Length + 1 ); }
							catch { active = true; }
						}
					}

					chainTaken[indentation] = taken || active;
					blockIndentations.Add( indentation );
					blockActive.Add( active );
				}
			}

			return options;
		}

		private ChoiceOption ParseChoiceOption( string line, int lineNumber, bool enclosingActive )
		{
			ChoiceOption option = new ChoiceOption() { line = lineNumber, hidden = !enclosingActive };
			int index = SkipWhitespace( line, 0 );
			while( index < line.Length && line[index] == '*' )
			{
				string command = GetCommandName( line.Substring( index ) );
				if( string.IsNullOrEmpty( command ) )
					break;

				index += command.Length + 1;
				if( command == "if" || command == "selectable_if" )
				{
					int conditionStart = index;
					bool met = true;
					try { met = EvaluateExpression( line, ref index, 0, EvaluateFlags.StopAtFirstToken ).AsBool(); }
					catch { index = Math.Max( index, conditionStart ); }

					if( !met && !option.hidden && !option.disabled )
					{
						option.hidden = command == "if";
						option.disabled = command == "selectable_if";
						option.condition = line.Substring( conditionStart, Math.Min( index, line.Length ) - conditionStart ).Trim();
					}
				}

				index = SkipWhitespace( line, index );
			}

			int hash = line.IndexOf( '#', Math.Min( index, line.Length ) );
			option.text = ( hash >= 0 ) ? line.Substring( hash + 1 ).Trim() : line;
			return option;
		}

		/// Writes what changed between two states: stats always, temps only while still in the same scene (a new scene resets them).
		private void AppendChanges( JSONNode before, JSONNode after, bool sceneChanged, string pad, StringBuilder tree )
		{
			List<string> changes = new List<string>( 4 );
			CollectChanges( before["stats"], after["stats"], false, changes );
			if( !sceneChanged )
				CollectChanges( before["temps"], after["temps"], true, changes );

			if( changes.Count == 0 )
				tree.Append( pad ).Append( "<color=" ).Append( DimColor ).Append( ">no stat changes</color>\n" );
			else
				tree.Append( pad ).Append( string.Join( ", ", changes.ToArray() ) ).Append( '\n' );
		}

		private void CollectChanges( JSONNode before, JSONNode after, bool temps, List<string> changes )
		{
			foreach( KeyValuePair<string, JSONNode> kvPair in after )
			{
				string key = kvPair.Key;
				if( key == "sceneName" || key.StartsWith( "choice_", StringComparison.OrdinalIgnoreCase ) || key == "implicit_control_flow" || ( temps && key.StartsWith( "param", StringComparison.OrdinalIgnoreCase ) ) )
					continue;

				JSONNode newValue = kvPair.Value;
				if( newValue.IsArray || newValue.IsObject )
					continue;

				bool existed = before.HasKey( key );
				JSONNode oldValue = existed ? before[key] : null;
				if( existed && oldValue.ToString() == newValue.ToString() )
					continue;

				StringBuilder change = new StringBuilder( 48 );
				change.Append( "<color=" ).Append( temps ? DimColor : VariableSetterLineColor ).Append( '>' ).Append( key ).Append( "</color> " );
				if( existed )
					change.Append( FormatValue( oldValue ) ).Append( " -> " );

				change.Append( "<b>" ).Append( FormatValue( newValue ) ).Append( "</b>" );
				if( existed && IsNumeric( oldValue ) && IsNumeric( newValue ) )
				{
					double delta = AsNumber( newValue ) - AsNumber( oldValue );
					change.Append( " <color=" ).Append( delta >= 0 ? OtherCommandLineColor : VariableValueColor ).Append( ">(" ).Append( delta >= 0 ? "+" : "" ).Append( Math.Round( delta, 2 ) ).Append( ")</color>" );
				}

				changes.Add( change.ToString() );
			}
		}

		private static bool IsNumeric( JSONNode node )
		{
			double value;
			return node.IsNumber || ( node.IsString && double.TryParse( node.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value ) );
		}

		private static double AsNumber( JSONNode node )
		{
			return node.IsNumber ? node.AsDouble : double.Parse( node.Value, System.Globalization.CultureInfo.InvariantCulture );
		}

		private static string FormatValue( JSONNode node )
		{
			if( node.IsString )
				return ( node.Value.Length > 40 ) ? "\"" + node.Value.Substring( 0, 40 ) + "...\"" : "\"" + node.Value + "\"";
			if( node.IsNumber )
				return Math.Round( node.AsDouble, 2 ).ToString( System.Globalization.CultureInfo.InvariantCulture );

			return node.ToString();
		}

		private static void MergePreviousBooksStats( JSONNode state )
		{
			JSONNode value;
			if( state.TryGetValue( "undeleted", out value ) && value.TryGetValue( "startingStats", out value ) )
			{
				foreach( KeyValuePair<string, JSONNode> kvPair in value )
				{
					if( !state["stats"].HasKey( kvPair.Key ) )
						state["stats"][kvPair.Key] = kvPair.Value;
				}
			}
		}

		private void SetTreeEntries( string[] texts )
		{
			foreach( Text oldEntry in treeEntries )
			{
				oldEntry.gameObject.SetActive( false );
				oldEntry.rectTransform.SetAsLastSibling();
				pooledEntries.Push( oldEntry );
			}

			treeEntries.Clear();

			for( int i = 0; i < texts.Length; i++ )
			{
				Text entry;
				if( pooledEntries.Count > 0 )
				{
					entry = pooledEntries.Pop();
					entry.gameObject.SetActive( true );
				}
				else
					entry = Instantiate( entryPrefab, scrollView.content, false );

				entry.fontSize = FontSize;
				entry.horizontalOverflow = WrapLines ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
				entry.text = texts[i] + ( i == texts.Length - 1 ? "\n<color=" + DimColor + ">---------- source ----------</color>" : "" );
				entry.rectTransform.SetSiblingIndex( i ); // Above the "load more lines" button and the source lines
				treeEntries.Add( entry );
			}
		}
	}
}
