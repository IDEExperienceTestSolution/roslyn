﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Indentation;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.CSharp.StringCopyPaste
{
    using static StringCopyPasteHelpers;

    /// <summary>
    /// Paste processor responsible for determining how text should be treated if it came from a source outside of the
    /// editor we're in.  In that case, we don't know what any particular piece of text means.  For example, <c>\t</c>
    /// might be a tab or it could be the literal two characters <c>\</c> and <c>t</c>.
    /// </summary>
    internal class UnknownSourcePasteProcessor : AbstractPasteProcessor
    {
        public UnknownSourcePasteProcessor(
            ITextSnapshot snapshotBeforePaste,
            ITextSnapshot snapshotAfterPaste,
            Document documentBeforePaste,
            Document documentAfterPaste,
            ExpressionSyntax stringExpressionBeforePaste,
            string newLine)
            : base(snapshotBeforePaste, snapshotAfterPaste, documentBeforePaste, documentAfterPaste, stringExpressionBeforePaste, newLine)
        {
        }

        public ImmutableArray<TextChange> GetTextChanges(CancellationToken cancellationToken)
        {
            // If we have a raw-string, then we always want to check for changes to make, even if the paste was
            // technically legal.  This is because we may want to touch up things like indentation to make the
            // pasted text look good for raw strings.
            //
            // Check for certain things we always think we should escape.
            if (!IsAnyRawStringExpression(StringExpressionBeforePaste) && !ShouldAlwaysEscapeTextForNonRawString())
            {
                // If the pasting was successful, then no need to change anything.
                if (PasteWasSuccessful(cancellationToken))
                    return default;
            }

            // Ok, the user pasted text that couldn't cleanly be added to this token without issue. Repaste the
            // contents, but this time properly escapes/manipulated so that it follows the rule of the particular token
            // kind.
            return GetAppropriateTextChanges(cancellationToken);
        }

        private bool ShouldAlwaysEscapeTextForNonRawString()
        {
            if (StringExpressionBeforePaste is LiteralExpressionSyntax literal)
            {
                // Pasting a control character into a normal string literal is normally not desired.  So even if this
                // is legal, we still escape the contents to make the pasted code clear.
                return literal.Token.IsRegularStringLiteral() && ContainsControlCharacter(Changes);
            }
            else if (StringExpressionBeforePaste is InterpolatedStringExpressionSyntax interpolatedString)
            {
                // Pasting a control character into a normal string literal is normally not desired.  So even if this
                // is legal, we still escape the contents to make the pasted code clear.
                return interpolatedString.StringStartToken.IsKind(SyntaxKind.InterpolatedStringStartToken) && ContainsControlCharacter(Changes);
            }

            throw ExceptionUtilities.UnexpectedValue(StringExpressionBeforePaste);
        }

        private ImmutableArray<TextChange> GetAppropriateTextChanges(CancellationToken cancellationToken)
        {
            // For pastes into non-raw strings, we can just determine how the change should be escaped in-line at that
            // same location the paste originally happened at.  For raw-strings things get more complex as we have to
            // deal with things like indentation and potentially adding newlines to make things legal.
            if (StringExpressionBeforePaste is LiteralExpressionSyntax literalExpression)
            {
                if (literalExpression.Token.Kind() == SyntaxKind.StringLiteralToken)
                    return GetEscapedTextChangesForNonRawStringLiteral(literalExpression.Token.IsVerbatimStringLiteral());

                if (literalExpression.Token.Kind() is SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken)
                    return GetTextChangesForRawStringLiteral(cancellationToken);
            }
            else if (StringExpressionBeforePaste is InterpolatedStringExpressionSyntax interpolatedString)
            {
                if (interpolatedString.StringStartToken.Kind() == SyntaxKind.InterpolatedStringStartToken)
                    return GetEscapedTextChangesForNonRawStringLiteral(isVerbatim: false);

                if (interpolatedString.StringStartToken.Kind() == SyntaxKind.InterpolatedVerbatimStringStartToken)
                    return GetEscapedTextChangesForNonRawStringLiteral(isVerbatim: true);

                // todo: add support for pasting into a interpolated raw string.
            }

            return default;
        }

        private ImmutableArray<TextChange> GetEscapedTextChangesForNonRawStringLiteral(bool isVerbatim)
        {
            using var _ = ArrayBuilder<TextChange>.GetInstance(out var textChanges);

            foreach (var change in Changes)
                textChanges.Add(new TextChange(change.OldSpan.ToTextSpan(), EscapeForNonRawStringLiteral(isVerbatim, change.NewText)));

            return textChanges.ToImmutable();
        }

        /// <summary>
        /// Given an initial raw string literal, and the changes made to it by the paste, determines how many quotes to
        /// add to the start and end to keep things parsing properly.
        /// </summary>
        private string? GetQuotesToAddToRawLiteral()
        {
            var longestQuoteSequence = TextContentsSpansAfterPaste.Max(ts => GetLongestQuoteSequence(SnapshotAfterPaste.GetSpan(ts.ToSpan())));

            var quotesToAddCount = (longestQuoteSequence - delimiterQuoteCount) + 1;
            if (quotesToAddCount <= 0)
                return null;

            return new string('"', quotesToAddCount);
        }

        private ImmutableArray<TextChange> GetTextChangesForRawStringLiteral(CancellationToken cancellationToken)
        {
            // Can't really figure anything out if the raw string is in error.
            if (NodeOrTokenContainsError(StringExpressionBeforePaste))
                return default;

            // If all we're going to do is insert whitespace, then don't make any adjustments to the text. We don't want
            // to end up inserting nothing and having the user very confused why their paste did nothing.
            if (AllWhitespace(SnapshotBeforePaste.Version.Changes))
                return default;

            // if the content we're going to add itself contains quotes, then figure out how many start/end quotes the
            // final string literal will need (which also gives us the number of quotes to add to teh start/end).
            var quotesToAdd = GetQuotesToAddToRawLiteral(stringLiteralExpressionBeforePaste);
            return stringLiteralExpressionBeforePaste.Token.Kind() is SyntaxKind.SingleLineRawStringLiteralToken
                ? GetTextChangesForSingleLineRawStringLiteral(quotesToAdd, cancellationToken)
                : GetTextChangesForMultiLineRawStringLiteral(quotesToAdd);
        }

        // Pasting with single line case.

        private ImmutableArray<TextChange> GetTextChangesForSingleLineRawStringLiteral(
            string? quotesToAdd,
            CancellationToken cancellationToken)
        {
            // When pasting into a single-line raw literal we will keep it a single line if we can.  If the content
            // we're pasting starts/ends with a quote, or contains a newline, then we have to convert to a multiline.
            //
            // Pasting any other content into a single-line raw literal is always legal and needs no extra work on our
            // part.

            var contentSpan = GetRawStringLiteralTextContentSpan(SnapshotBeforePaste.AsText(), stringLiteralExpressionBeforePaste, out _);
            var contentSpanAfterPaste = MapSpanForward(contentSpan);
            var mustBeMultiLine = RawContentMustBeMultiLine(SnapshotAfterPaste.GetSpan(contentSpanAfterPaste.ToSpan()));

            var indentationWhitespace = stringLiteralExpressionBeforePaste.Token.GetPreferredIndentation(DocumentBeforePaste, cancellationToken);

            using var _1 = ArrayBuilder<TextChange>.GetInstance(out var finalTextChanges);
            using var _2 = PooledStringBuilder.GetInstance(out var buffer);

            // First, add any extra quotes if we need them.
            if (quotesToAdd != null)
                finalTextChanges.Add(new TextChange(new TextSpan(stringLiteralExpressionBeforePaste.SpanStart, 0), quotesToAdd));

            // Then a newline and the indentation to start with.
            if (mustBeMultiLine)
                finalTextChanges.Add(new TextChange(new TextSpan(contentSpan.Start, 0), NewLine + indentationWhitespace));

            SourceText? changeText = null;
            foreach (var change in Changes)
            {
                // Create a text object around the change text we're making.  This is a very simple way to get
                // a nice view of the text lines in the change.
                changeText = SourceText.From(change.NewText);
                var commonIndentationPrefix = GetCommonIndentationPrefix(changeText) ?? "";

                buffer.Clear();

                for (var i = 0; i < changeText.Lines.Count; i++)
                {
                    // The actual full line that was pasted in.
                    var currentChangeLine = changeText.Lines[i];
                    var fullChangeLineText = changeText.ToString(currentChangeLine.SpanIncludingLineBreak);

                    if (i == 0)
                    {
                        // on the first line, remove the common indentation if we can. Otherwise leave alone.
                        if (fullChangeLineText.StartsWith(commonIndentationPrefix, StringComparison.OrdinalIgnoreCase))
                            buffer.Append(fullChangeLineText[commonIndentationPrefix.Length..]);
                        else
                            buffer.Append(fullChangeLineText);
                    }
                    else
                    {
                        // on all the rest of the lines, always remove the common indentation.
                        buffer.Append(fullChangeLineText[commonIndentationPrefix.Length..]);
                    }

                    // if we ended with a newline, make sure the next line is indented enough.
                    if (HasNewLine(currentChangeLine))
                        buffer.Append(indentationWhitespace);
                }

                finalTextChanges.Add(new TextChange(change.OldSpan.ToTextSpan(), buffer.ToString()));
            }

            // if the last change ended at the closing delimiter *and* ended with a newline, then we don't need to add a
            // final newline-space at the end because we will have already done that.
            if (mustBeMultiLine && !LastPastedLineAddedNewLine())
                finalTextChanges.Add(new TextChange(new TextSpan(contentSpan.End, 0), NewLine + indentationWhitespace));

            if (quotesToAdd != null)
                finalTextChanges.Add(new TextChange(new TextSpan(stringLiteralExpressionBeforePaste.Span.End, 0), quotesToAdd));

            return finalTextChanges.ToImmutable();

            bool LastPastedLineAddedNewLine()
            {
                return changeText != null &&
                    Changes.Last().OldEnd == contentSpan.End &&
                      HasNewLine(changeText.Lines.Last());
            }
        }

        private static bool RawContentMustBeMultiLine(SnapshotSpan snapshotSpan)
        {
            // Empty raw string must be multiline.
            if (snapshotSpan.IsEmpty)
                return true;

            // Or if it starts/ends with a quote 
            if (snapshotSpan.Start.GetChar() == '"')
                return true;

            if ((snapshotSpan.End - 1).GetChar() == '"')
                return true;

            // or contains a newline
            for (var i = snapshotSpan.Span.Start; i < snapshotSpan.Span.End; i++)
            {
                if (SyntaxFacts.IsNewLine(snapshotSpan.Snapshot[i]))
                    return true;
            }

            return false;
        }

        // Pasting into multi line case.

        private ImmutableArray<TextChange> GetTextChangesForMultiLineRawStringLiteral(
            string? quotesToAdd)
        {
            var textBeforePaste = SnapshotBeforePaste.AsText();
            var endLine = textBeforePaste.Lines.GetLineFromPosition(StringExpressionBeforePaste.Span.End);

            // The indentation whitespace every line of the final raw string needs.
            var indentationWhitespace = endLine.GetLeadingWhitespace();

            using var _1 = ArrayBuilder<TextChange>.GetInstance(out var finalTextChanges);
            using var _2 = PooledStringBuilder.GetInstance(out var buffer);

            if (quotesToAdd != null)
                finalTextChanges.Add(new TextChange(new TextSpan(StringExpressionBeforePaste.SpanStart, 0), quotesToAdd));

            for (var changeIndex = 0; changeIndex < Changes.Count; changeIndex++)
            {
                var change = Changes[changeIndex];

                // Create a text object around the change text we're making.  This is a very simple way to get
                // a nice view of the text lines in the change.
                var changeText = SourceText.From(change.NewText);
                buffer.Clear();

                var commonIndentationPrefix = GetCommonIndentationPrefix(changeText);

                for (var lineIndex = 0; lineIndex < changeText.Lines.Count; lineIndex++)
                {
                    var firstChange = changeIndex == 0 && lineIndex == 0;
                    var lastChange = (changeIndex == Changes.Count - 1) &&
                                     (lineIndex == changeText.Lines.Count - 1);

                    // The actual full line that was pasted in.
                    var currentChangeLine = changeText.Lines[lineIndex];
                    var fullChangeLineText = changeText.ToString(currentChangeLine.SpanIncludingLineBreak);
                    // The contents of the line, with all leading whitespace removed.
                    var (lineLeadingWhitespace, lineWithoutLeadingWhitespace) = ExtractWhitespace(fullChangeLineText);

                    // This entire if-chain is only concerned with inserting the necessary whitespace a line should have.

                    if (firstChange)
                    {
                        // The first line is often special.  It may be copied without any whitespace (e.g. the user
                        // starts their selection at the start of text on that line, not the start of the line itself).
                        // So we use some heuristics to try to decide what to do depending on how much whitespace we see
                        // on that first copied line.

                        textBeforePaste.GetLineAndOffset(change.OldSpan.Start, out var line, out var offset);

                        // First, ensure that the indentation whitespace of the *inserted* first line is sufficient.
                        if (line == textBeforePaste.Lines.GetLineFromPosition(StringExpressionBeforePaste.SpanStart).LineNumber)
                        {
                            // if the first chunk was pasted into the space after the first `"""` then we need to actually
                            // insert a newline, then the indentation whitespace, then the first line of the change.
                            buffer.Append(NewLine);
                            buffer.Append(indentationWhitespace);
                        }
                        else if (offset < indentationWhitespace.Length)
                        {
                            // On the first line, we were pasting into the indentation whitespace.  Ensure we add enough
                            // whitespace so that the trimmed line starts at an acceptable position.
                            buffer.Append(indentationWhitespace[offset..]);
                        }

                        // Now, we want to actually insert any whitespace the line itself should have *if* it's got more
                        // than the common indentation whitespace.
                        if (commonIndentationPrefix != null && lineLeadingWhitespace.StartsWith(commonIndentationPrefix, StringComparison.OrdinalIgnoreCase))
                            buffer.Append(lineLeadingWhitespace[commonIndentationPrefix.Length..]);
                    }
                    else if (!lastChange && lineWithoutLeadingWhitespace.Length > 0 && SyntaxFacts.IsNewLine(lineWithoutLeadingWhitespace[0]))
                    {
                        // if it's just an empty line, don't bother adding any whitespace at all.  This will just end up
                        // inserting the blank line here.  We don't do this on the last line as we want to still insert
                        // the right amount of indentation so that the user's caret is placed properly in the text.  We
                        // could technically not insert the whitespace and attempt to place the caret using a virtual
                        // position, but this adds a lot of complexity to this system, so we avoid that for now and go
                        // for the simpler approach..
                    }
                    else
                    {
                        // On any other line we're adding, ensure we have enough indentation whitespace to proceed.
                        // Add the necessary whitespace the literal needs, then add the line contents without 
                        // the common whitespace included.
                        buffer.Append(indentationWhitespace);
                        if (commonIndentationPrefix != null)
                            buffer.Append(lineLeadingWhitespace[commonIndentationPrefix.Length..]);
                    }

                    // After the necessary whitespace has been added, add the actual non-whitespace contents of the
                    // line.
                    buffer.Append(lineWithoutLeadingWhitespace);

                    if (lastChange)
                    {
                        // Similar to the check we do for the first-change, if the last change was pasted into the space
                        // before the last `"""` then we need potentially insert a newline, then enough indentation
                        // whitespace to keep delimiter in the right location.

                        textBeforePaste.GetLineAndOffset(change.OldSpan.End, out var line, out var offset);

                        if (line == textBeforePaste.Lines.GetLineFromPosition(StringExpressionBeforePaste.Span.End).LineNumber)
                        {
                            if (!HasNewLine(currentChangeLine))
                                buffer.Append(NewLine);

                            buffer.Append(textBeforePaste.ToString(new TextSpan(textBeforePaste.Lines[line].Start, offset)));
                        }
                    }
                }

                finalTextChanges.Add(new TextChange(change.OldSpan.ToTextSpan(), buffer.ToString()));
            }

            if (quotesToAdd != null)
                finalTextChanges.Add(new TextChange(new TextSpan(StringExpressionBeforePaste.Span.End, 0), quotesToAdd));

            return finalTextChanges.ToImmutable();
        }
    }
}
