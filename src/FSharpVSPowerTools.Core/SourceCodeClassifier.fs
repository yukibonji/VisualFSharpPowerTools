﻿namespace FSharpVSPowerTools

open System
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpVSPowerTools
open System.Collections.Generic

[<RequireQualifiedAccess>]
type Category =
    | ReferenceType
    | ValueType
    | PatternCase
    | Function
    | MutableVar
    | Quotation
    | Module
    | Unused
    | Printf
    | Other
    override x.ToString() = sprintf "%A" x

type CategorizedColumnSpan =
    { Category: Category
      WordSpan: WordSpan }

module QuotationCategorizer =
    let private categorize (lexer: LexerBase) ranges =
        let trimWhitespaces = 
            Seq.skipWhile (fun t -> t.CharClass = TokenCharKind.WhiteSpace) >> Seq.toList

        ranges
        |> Seq.map (fun (r: Range.range) -> 
            if r.EndLine = r.StartLine then
                seq [ { Category = Category.Quotation
                        WordSpan = { Line = r.StartLine
                                     StartCol = r.StartColumn
                                     EndCol = r.EndColumn }} ]
            else
                [r.StartLine..r.EndLine]
                |> Seq.choose (fun line ->
                     let tokens = lexer.TokenizeLine (line - 1)

                     let tokens =
                        match tokens |> List.tryFind (fun t -> t.TokenName = "RQUOTE") with
                        | Some rquote -> 
                            tokens
                            |> List.rev
                            |> Seq.skipWhile ((<>) rquote)
                            |> Seq.toList
                            |> List.rev
                        | _ -> 
                            match tokens |> List.tryFind (fun t -> t.TokenName = "LQUOTE") with
                            | Some lquote -> tokens |> Seq.skipWhile (fun t -> t <> lquote) |> Seq.toList
                            | _ -> tokens 

                     let tokens = tokens |> trimWhitespaces |> List.rev |> trimWhitespaces |> List.rev
                     
                     match tokens with
                     | [] -> None
                     | _ ->
                        let minCol = tokens |> List.map (fun t -> t.LeftColumn) |> function [] -> 0 | xs -> xs |> List.min
                 
                        let maxCol = 
                            let tok = tokens |> List.maxBy (fun t -> t.RightColumn) 
                            tok.LeftColumn + tok.FullMatchedLength

                        Some { Category = Category.Quotation
                               WordSpan = { Line = line
                                            StartCol = minCol
                                            EndCol = maxCol }}))
        |> Seq.concat
        |> Seq.toArray

    let getCategories ast lexer = UntypedAstUtils.getQuatationRanges ast |> categorize lexer

module private PrintfCategorizer =
    let private printfFunctions = ["printf";"printfn";"sprintf";"failwithf";"eprintf";"eprintfn"] |> set
    let private printfTerminators = ['b';'c';'s';'d';'i';'u';'x';'X';'o';'e';'E';'f';'g';'G';'M';'O';'A';'a';'t'] |> set
    let private printfModifiers   = ['0';'1';'2';'3';'4';'5';'6';'7';'8';'9';'-';'+';' '] |> set
        
    let private categorize (lexer: LexerBase) getTextLine (idents: Ident list) =
        idents 
        |> Seq.choose (fun ident -> if printfFunctions |> Set.contains ident.idText then Some ident.idRange else None)
        |> Seq.map (fun (r: Range.range) -> seq {

            // TODO: Multi-line printf formats
            let line: string = getTextLine (r.EndLine - 1)

            let lit = lexer.TokenizeLine (r.EndLine - 1)
                        |> Seq.skipWhile (fun t -> t.LeftColumn < r.EndColumn || t.TokenName <> "STRING_TEXT")
                        |> Seq.takeWhile (fun t -> t.TokenName = "STRING_TEXT")
                        |> Seq.fold (fun (left,right) (tok) ->
                        min left tok.LeftColumn, 
                        max right tok.RightColumn
                        ) (9999,0)

            match lit with 
            | (9999,_) -> ()
            | (left, right) ->
                let formatter = line.Substring (left + 1, right - left)
                let findLengthAndSkip i = 
                    let rec findTerminator i = 
                        if formatter.Length <= i then 0 else
                        let c = formatter.[i]
                        if printfTerminators.Contains c then i + 1
                        elif printfModifiers.Contains c then findTerminator (i + 1)
                        else 0
                    if formatter.[i] = '%' then 2, None else
                    match findTerminator i with
                    | 0  -> 1, None
                    | i' -> (i' + 1 - i), Some (i' + 1 - i)

                let rec parseFmt acc i =
                    if i >= (formatter.Length - 1) then acc else
                    match formatter.[i] with
                    | '%' -> 
                        let skip, len = findLengthAndSkip (i + 1) 
                        match len with 
                        | Some l -> 
                            let hit = { Category = Category.Printf
                                        WordSpan = { Line = r.EndLine
                                                     StartCol = left + i + 1
                                                     EndCol = left + i + l + 1 }} 

                            parseFmt (hit::acc) (i + skip)
                        | _ -> parseFmt acc (i + skip)
                    | _ -> parseFmt acc (i + 1)
                yield! (parseFmt [] 0)
            | _ -> ()
            })
        |> Seq.concat
        |> Seq.toArray

    let getCategories ast lexer getTextLine = UntypedAstUtils.getIdents ast |> categorize lexer getTextLine

module SourceCodeClassifier =
    let getIdentifierCategory = function
        | Entity (_, ValueType, _) -> Category.ValueType
        | Entity Class -> Category.ReferenceType
        | Entity (_, FSharpModule, _) -> Category.Module
        | Entity (_, _, Tuple) -> Category.ReferenceType
        | Entity (_, (FSharpType | ProvidedType | ByRef | Array), _) -> Category.ReferenceType    
        | _ -> Category.Other 

    // Filter out symbols which ranges are fully included into a bigger symbols. 
    // For example, for this code: Ns.Module.Module1.Type.NestedType.Method() FCS returns the following symbols: 
    // Ns, Module, Module1, Type, NestedType, Method.
    // We want to filter all of them but the longest one (Method).
    let filterNestedSymbolUses (longIdents: IDictionary<_,_>) symbolUses =
        symbolUses
        |> Array.map (fun sUse ->
            match longIdents.TryGetValue sUse.SymbolUse.RangeAlternate.End with
            | true, longIdent -> sUse, Some longIdent
            | _ -> sUse, None)
        |> Seq.groupBy (fun (_, longIdent) -> longIdent)
        |> Seq.map (fun (longIdent, sUses) -> longIdent, sUses |> Seq.map fst)
        |> Seq.map (fun (longIdent, symbolUses) ->
            match longIdent with
            | Some _ -> 
                (* Find all longest SymbolUses which has unique roots. For example:
                           
                    module Top
                    module M =
                        type System.IO.Path with
                            member static ExtensionMethod() = ()

                    open M
                    open System
                    let _ = IO.Path.ExtensionMethod()

                    The last line contains three SymbolUses: "System.IO", "System.IO.Path" and "Top.M.ExtensionMethod". 
                    So, we filter out "System.IO" since it's a prefix of "System.IO.Path".

                *)
                let res =
                    symbolUses
                    |> Seq.sortBy (fun symbolUse -> -symbolUse.SymbolUse.RangeAlternate.EndColumn)
                    |> Seq.fold (fun (prev, acc) next ->
                         match prev with
                         | Some prev -> 
                            if prev.FullNames
                               |> Array.exists (fun prevFullName ->
                                    next.FullNames
                                    |> Array.exists (fun nextFullName ->
                                         nextFullName.Length < prevFullName.Length
                                         && prevFullName |> Array.startsWith nextFullName)) then 
                                Some prev, acc
                            else Some next, next :: acc
                         | None -> Some next, next :: acc)
                       (None, [])
                    |> snd
                    |> List.rev
                    |> List.toSeq
                res
            | None -> symbolUses)
        |> Seq.concat
        |> Seq.toArray

    let getSymbolUsesPotentiallyRequireOpenDecls symbolsUses =
        symbolsUses
        |> Array.filter (fun symbolUse ->
            let res = 
                match symbolUse.SymbolUse.Symbol with
                | Pattern | RecordField _
                | Entity (Class | (AbbreviatedType _ | FSharpType | ValueType | FSharpModule | Array), _, _)
                | Entity (_, _, Tuple)
                | MemberFunctionOrValue (Constructor _ | ExtensionMember) -> true
                | MemberFunctionOrValue func -> not func.IsMember
                | _ -> false
            res)
        //|> Array.map (fun (symbolUse, _) -> symbolUse)

    let internal getCategory (symbolUse: FSharpSymbolUse) =
        match symbolUse.Symbol with
        | Field (MutableVar, _)
        | Field (_, RefCell) -> Category.MutableVar
        | Pattern -> Category.PatternCase
        | Entity (_, ValueType, _) -> Category.ValueType
        | Entity Class -> Category.ReferenceType
        | Entity (_, FSharpModule, _) -> Category.Module
        | Entity (_, _, Tuple) -> Category.ReferenceType
        | Entity (_, (FSharpType | ProvidedType | ByRef | Array), _) -> Category.ReferenceType
        | MemberFunctionOrValue (Constructor ValueType) -> Category.ValueType
        | MemberFunctionOrValue (Constructor _) -> Category.ReferenceType
        | MemberFunctionOrValue (Function symbolUse.IsFromComputationExpression) -> Category.Function
        | MemberFunctionOrValue MutableVar -> Category.MutableVar
        | MemberFunctionOrValue func ->
            match func.FullTypeSafe with
            | Some RefCell -> Category.MutableVar
            | _ -> Category.Other
        | _ -> Category.Other 

    // If "what" span is entirely included in "from" span, then truncate "from" to the end of "what".
    // Example: for ReferenceType symbol "System.Diagnostics.DebuggerDisplay" there are "System", "Diagnostics" and "DebuggerDisplay"
    // plane symbols. After excluding "System", we get "Diagnostics.DebuggerDisplay",
    // after excluding "Diagnostics", we get "DebuggerDisplay" and we are done.
    let excludeWordSpan from what =
        if what.EndCol < from.EndCol && what.EndCol > from.StartCol then
            { from with StartCol = what.EndCol + 1 } // the dot between parts
        else from


    let getFullPrefix (longIdents: IDictionary<_,_>) fullName (endPos: Range.pos): Idents option =
        match longIdents.TryGetValue endPos with
        | true, longIdent ->
            let rec loop matchFound longIdents symbolIdents =
                match longIdents, symbolIdents with
                | [], _ -> symbolIdents
                | _, [] -> []
                | lh :: lt, sh :: st ->
                    if lh <> sh then
                        if matchFound then symbolIdents else loop matchFound lt symbolIdents
                    else loop true lt st
                        
            let prefix = 
                loop false (longIdent |> Array.rev |> List.ofArray) (fullName |> Array.rev |> List.ofArray)
                |> List.rev
                |> List.toArray
                            
//            debug "[SourceCodeClassifier] QualifiedSymbol: FullName = %A, Symbol end pos = (%d, %d), Res = %A" 
//                  fullName endPos.Line endPos.Column prefix
            Some prefix
        | _ -> None

    let getCategoriesAndLocations (allSymbolsUses: SymbolUse[], ast: ParsedInput option, lexer: LexerBase, getTextLine: int -> string,
                                   openDeclarations: OpenDeclaration list, allEntities: Map<string, Idents list> option) =
        let allSymbolsUses' =
            allSymbolsUses
            |> Seq.groupBy (fun su -> su.SymbolUse.RangeAlternate.EndLine)
            |> Seq.map (fun (line, sus) ->
                let tokens = lexer.TokenizeLine (line - 1)
                sus
                |> Seq.choose (fun su ->
                    let r = su.SymbolUse.RangeAlternate
                    lexer.GetSymbolFromTokensAtLocation (tokens, line - 1, r.End.Column - 1)
                    |> Option.bind (fun sym -> 
                        match sym.Kind with
                        | SymbolKind.Ident ->
                            // FCS returns inaccurate ranges for multiline method chains
                            // Specifically, only the End is right. So we use the lexer to find Start for such symbols.
                            if r.StartLine < r.EndLine then
                                Some (su, { Line = r.End.Line; StartCol = r.End.Column - sym.Text.Length; EndCol = r.End.Column })
                            else 
                                Some (su, { Line = r.End.Line; StartCol = r.Start.Column; EndCol = r.End.Column })
                        | _ -> None)))
            |> Seq.concat
            |> Seq.toArray
       
        // index all symbol usages by LineNumber 
        let wordSpans = 
            allSymbolsUses'
            |> Seq.map (fun (_, span) -> span)
            |> Seq.groupBy (fun span -> span.Line)
            |> Seq.map (fun (line, ranges) -> line, ranges)
            |> Map.ofSeq

        let spansBasedOnSymbolsUses = 
            allSymbolsUses'
            |> Seq.choose (fun (symbolUse, span) ->
                let span = 
                    match wordSpans.TryFind span.Line with
                    | Some spans -> spans |> Seq.fold (fun result span -> excludeWordSpan result span) span
                    | _ -> span

                let span' = 
                    if (span.EndCol - span.StartCol) - symbolUse.SymbolUse.Symbol.DisplayName.Length > 0 then
                        // The span is wider that the simbol's display name.
                        // This means that we have not managed to extract last part of a long ident accurately.
                        // Particulary, it happens for chained method calls like Guid.NewGuid().ToString("N").Substring(1).
                        // So we get ident from the lexer.
                        match lexer.GetSymbolAtLocation (span.Line - 1, span.EndCol - 1) with
                        | Some s -> 
                            match s.Kind with
                            | Ident -> 
                                // Lexer says that our span is too wide. Adjust it's left column.
                                if span.StartCol < s.LeftColumn then { span with StartCol = s.LeftColumn }
                                else span
                            | _ -> span
                        | _ -> span
                    else span

                let categorizedSpan =
                    if span'.EndCol <= span'.StartCol then None
                    else Some { Category = 
                                    if not symbolUse.IsUsed then Category.Unused 
                                    else getCategory symbolUse.SymbolUse
                                WordSpan = span' }
        
                categorizedSpan)
            |> Seq.groupBy (fun span -> span.WordSpan)
            |> Seq.map (fun (_, spans) -> 
                    match List.ofSeq spans with
                    | [span] -> span
                    | spans -> 
                        spans 
                        |> List.sortBy (fun span -> 
                            match span.Category with
                            | Category.Unused -> 1
                            | Category.Other -> 2
                            | _ -> 0)
                        |> List.head)
            |> Seq.distinct
            |> Seq.toArray

        let longIdentsByEndPos = UntypedAstUtils.getLongIdents ast
            
        //| SynExpr.Ident (ident) -> if printfFunctions.Contains ident.idText then (!printfRanges).Add ident.idRange

//        debug "LongIdents by line:" 
//        longIdentsByEndPos 
//        |> Seq.map (fun pair -> pair.Key.Line, pair.Key.Column, pair.Value) 
//        |> Seq.iter (debug "%A")

        let removeModuleSuffixes (symbolUses: SymbolUse[]) =
            match allEntities with
            | Some entities ->
                symbolUses 
                |> Array.map (fun symbolUse ->
                    let fullNames =
                        symbolUse.FullNames
                        |> Array.map (fun fullName ->
                            match entities |> Map.tryFind (String.Join (".", fullName)) with
                            | Some [cleanIdents] ->
                                debug "[SourceCodeClassifier] One clean FullName %A -> %A" fullName cleanIdents
                                cleanIdents
                            | Some (firstCleanIdents :: _ as cleanIdentsList) ->
                                if cleanIdentsList |> List.exists ((=) fullName) then
                                    debug "[SourceCodeClassifier] An exact match found among several clean idents: %A" fullName
                                    fullName
                                else
                                    debug "[SourceCodeClassifier] No match found among several clean idents, return the first one FullName %A -> %A" 
                                          fullName firstCleanIdents
                                    firstCleanIdents
                            | _ -> 
                                debug "[SourceCodeClassifier] NOT Cleaned FullName %A" fullName
                                fullName)
                    { symbolUse with FullNames = fullNames })
            | None -> symbolUses

//        let printSymbolUses msg (symbolUses: SymbolUse[]) =
//            debug "[SourceCodeClassifier] %s SymbolUses:\n" msg
//            for sUse in symbolUses do
//                let r = sUse.SymbolUse.RangeAlternate
//                debug "%A (%d, %d) -- (%d, %d)" sUse.FullNames r.StartLine r.StartColumn r.EndLine r.EndColumn
//            symbolUses

        let symbolPrefixes: (Range.range * Idents) [] =
            allSymbolsUses
            |> getSymbolUsesPotentiallyRequireOpenDecls
            //|> printSymbolUses "SymbolUsesPotentiallyRequireOpenDecls"
            |> removeModuleSuffixes
            //|> printSymbolUses "SymbolUsesWithModuleSuffixedRemoved"
            |> filterNestedSymbolUses longIdentsByEndPos
            //|> printSymbolUses "SymbolUses without nested"
            |> Array.map (fun symbolUse ->
                let sUseRange = symbolUse.SymbolUse.RangeAlternate
                symbolUse.FullNames
                |> Array.choose (fun fullName ->
                    getFullPrefix longIdentsByEndPos fullName sUseRange.End
                    |> Option.bind (function
                         | [||] -> None
                         | prefix -> Some (sUseRange, prefix)))) 
            |> Array.concat

        debug "[SourceCodeClassifier] Symbols prefixes:\n%A,\nOpen declarations:\n%A" symbolPrefixes openDeclarations
        
        let openDeclarations = 
            Array.foldBack (fun (symbolRange: Range.range, symbolPrefix: Idents) openDecls ->
                openDecls |> OpenDeclarationGetter.updateOpenDeclsWithSymbolPrefix symbolPrefix symbolRange
            ) symbolPrefixes openDeclarations
            |> OpenDeclarationGetter.spreadIsUsedFlagToParents

        //debug "[SourceCodeClassifier] Fully processed open declarations:"
        //for decl in openDeclarations do debug "%A" decl

        let unusedOpenDeclarations: OpenDeclaration list =
            openDeclarations |> List.filter (fun decl -> not decl.IsUsed)

        //debug "[SourceCodeClassifier] Unused open declarations: %A" unusedOpenDeclarations

        let unusedOpenDeclarationSpans =
            unusedOpenDeclarations
            |> List.map (fun decl -> 
                { Category = Category.Unused
                  WordSpan = { Line = decl.DeclarationRange.StartLine 
                               StartCol = decl.DeclarationRange.StartColumn
                               EndCol = decl.DeclarationRange.EndColumn }})
            |> List.toArray
    
        //debug "[SourceCodeClassifier] AST: %A" ast

        let allSpans = 
            spansBasedOnSymbolsUses 
            |> Array.append (QuotationCategorizer.getCategories ast lexer)
            |> Array.append (PrintfCategorizer.getCategories ast lexer getTextLine)
            |> Array.append unusedOpenDeclarationSpans
            
    //    for span in allSpans do
    //       debug "-=O=- %A" span
        allSpans