﻿module Elmish.WPF.ViewModel

open System.ComponentModel
open System.Dynamic
open System.Windows

type PropertyAccessor<'model,'msg> =
    | Get of Getter<'model>
    | GetSet of Getter<'model> * Setter<'model,'msg>
    | Cmd of Command
    | Model of ViewModelBase<'model,'msg>
    | Map of Getter<'model> * (obj -> obj)

and ViewModelBase<'model, 'msg>(m:'model, dispatch, propMap: ViewBindings<'model,'msg>) as this =
    inherit DynamicObject()

    let propertyChanged = Event<PropertyChangedEventHandler,PropertyChangedEventArgs>()
    let notifyPropertyChanged name = 
        //console.log <| sprintf "Notify %s" name
        propertyChanged.Trigger(this,PropertyChangedEventArgs(name))

    let mutable model : 'model = m

    let props = new System.Collections.Generic.Dictionary<string, PropertyAccessor<'model,'msg>>()

    let notify (p:string list) =
        p |> List.iter notifyPropertyChanged
        let raiseCanExecuteChanged =
            function
            | Cmd c ->
                if isNull Application.Current then ()
                elif isNull Application.Current.Dispatcher then () else
                fun _ -> c.RaiseCanExecuteChanged()
                |> Application.Current.Dispatcher.Invoke
            | _ -> ()
        //TODO on raise for cmds that depend on props in p
        props |> List.ofSeq |> List.iter (fun kvp -> raiseCanExecuteChanged kvp.Value)

    let buildProps =
        let toCommand (exec, canExec) = Command((fun p -> exec p model |> dispatch), fun p -> canExec p model)
        let toSubView propMap = ViewModelBase<_,_>(model, dispatch, propMap)
        let rec convert = 
            List.map (fun (name,binding) ->
                match binding with
                | Bind getter -> name, Get getter
                | BindTwoWay (getter,setter) -> name, GetSet (getter,setter)
                | BindCmd (exec,canExec) -> name, Cmd <| toCommand (exec,canExec)
                | BindModel (_, propMap) -> name, Model <| toSubView propMap
                | BindMap (getter,mapper) -> name, Map <| (getter,mapper)
            )
        
        convert propMap |> List.iter (fun (n,a) -> props.Add(n,a))

    do buildProps

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member __.PropertyChanged = propertyChanged.Publish

    member __.UpdateModel other =
        //console.log <| sprintf "UpdateModel %A" (props.Keys |> Seq.toArray)
        let propDiff name =
            function
            | Get getter | GetSet (getter,_) | Map (getter,_) ->
                if getter model <> getter other then Some name else None
            | Model m ->
                m.UpdateModel other
                None
            | _ -> None

        let diffs = 
            props
            |> Seq.choose (fun (kvp) -> propDiff kvp.Key kvp.Value)
            |> Seq.toList
        
        model <- other
        notify diffs


    // DynamicObject overrides

    override __.TryGetMember (binder, r) = 
//        console.log <| sprintf "TryGetMember %s" binder.Name
        if props.ContainsKey binder.Name then
            r <-
                match props.[binder.Name] with 
                | Get getter 
                | GetSet (getter,_) -> getter model
                | Cmd c -> unbox c
                | Model m -> unbox m
                | Map (getter,mapper) -> getter model |> mapper
            true
        else false

    override __.TrySetMember (binder, value) =
        //console.log <| sprintf "TrySetMember %s" binder.Name
        if props.ContainsKey binder.Name then
            match props.[binder.Name] with 
            | GetSet (_,setter) -> try setter value model |> dispatch with | _ -> ()
            | _ -> invalidOp "Unable to set read-only member"
        false