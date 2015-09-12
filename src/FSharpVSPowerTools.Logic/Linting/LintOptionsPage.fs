﻿namespace FSharpVSPowerTools.Linting

open FSharpVSPowerTools
open Microsoft.VisualStudio.Shell
open System.ComponentModel
open System.IO
open System.Runtime.InteropServices
open System.Windows
open System.ComponentModel.Composition
open Microsoft.VisualStudio.OLE.Interop
open System.Collections.Generic
open FSharpLint.Framework.Configuration
open Management
open LintUtils

[<Guid("f0bb4785-e75a-485f-86e8-e382dd5934a4")>]
type LintOptionsPage(?dte:EnvDTE.DTE) =
    inherit UIElementDialogPage()

    let mutable loadedConfigs = LoadedConfigs.Empty

    let lintOptionsPageControl = lazy LintOptionsControlProvider()

    interface ILintOptions with
        member this.UpdateDirectories() =
            loadedConfigs <- updateLoadedConfigs (this.GetDte()) loadedConfigs

        member this.GetConfigurationForDirectory(dir) =
            getConfigForDirectory loadedConfigs dir

    member private this.GetDte() =
        match dte with
        | Some(dte) -> dte
        | None -> this.GetService(typeof<EnvDTE.DTE>) :?> EnvDTE.DTE
            
    override this.OnApply(_) = 
        match lintOptionsPageControl.Value.DataContext with
        | :? LintViewModel as viewModel ->
            match viewModel.ViewModel with
            | Some(optionsViewModel) -> 
                loadedConfigs <- saveViewModelToLoadedConfigs loadedConfigs optionsViewModel
                saveViewModel loadedConfigs optionsViewModel
            | None -> ()
        | _ -> ()

    override this.OnActivate(_) = 
        let dte = this.GetDte()

        loadedConfigs <- updateLoadedConfigs dte loadedConfigs
        loadedConfigs <- refresh tryLoadConfig loadedConfigs

        let lintOptions =
            match getInitialPath dte loadedConfigs with
            | Some(path) -> 
                OptionsViewModel(
                    getConfigForDirectory loadedConfigs,
                    getFileHierarchy loadedConfigs, 
                    [], 
                    path) |> Some
            | None -> 
                None

        lintOptionsPageControl.Value.DataContext <- LintViewModel(lintOptions)
            
    override this.Child = 
        let control = lintOptionsPageControl.Value
        control :> UIElement