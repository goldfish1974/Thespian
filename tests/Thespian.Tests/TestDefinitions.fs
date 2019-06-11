﻿module Nessos.Thespian.Tests.TestDefinitions

open System
open System.Runtime.Serialization
open Nessos.Thespian

type SerializeFailureControl =
    | NeverFail
    | FailOnSerialize
    | FailOnDeserialize of SerializeFailureControl

[<Serializable>]
type ControlledSerialializableException =
    inherit Exception
    val private control: ControlledSerializable

    new (msg: string, control: ControlledSerializable) =
        {
            inherit Exception(msg)
            control = control
        }

    new (info: SerializationInfo, context: StreamingContext) =
        {
            inherit Exception(info, context)
            control = info.GetValue("control", typeof<ControlledSerializable>) :?> ControlledSerializable
        }
    
    member self.Control = self.control

    interface ISerializable with
        override self.GetObjectData(info: SerializationInfo, context: StreamingContext) =
            info.AddValue("control", self.control)
            base.GetObjectData(info, context)

and [<Serializable>] ControlledSerializable(control: SerializeFailureControl) =
    new(info: SerializationInfo, context: StreamingContext) = new ControlledSerializable(ControlledSerializable.Deserialize info)

    static member Deserialize(info: SerializationInfo) =
        let control = info.GetValue("control", typeof<SerializeFailureControl>) :?> SerializeFailureControl
        match control with
        | FailOnDeserialize control -> raise <| new ControlledSerialializableException("Deserialization failure", new ControlledSerializable(control))
        | _ -> control    
    
    interface ISerializable with
        override __.GetObjectData(info: SerializationInfo, context: StreamingContext) =
            match control with
            | FailOnSerialize -> raise <| new ControlledSerialializableException("Serialization failure", new ControlledSerializable(NeverFail))
            | _ -> info.AddValue("control", control) 
        

type TestMessage<'T, 'R> = 
    | TestAsync of 'T
    | TestSync of IReplyChannel<'R> * 'T

type TestMessage<'T> = TestMessage<'T, unit>

type TestList<'T> = 
    | ListPrepend of 'T
    | Delay of int
    | ListGet of IReplyChannel<'T list>

type TestMultiReplies<'T> = 
    | MultiRepliesAsync of 'T
    | MultiRepliesSync of IReplyChannel<unit> * IReplyChannel<'T>

module PrimitiveBehaviors = 
    let nill (self : Actor<TestMessage<unit>>) = async.Zero()
    
    let consumeOne (self : Actor<TestMessage<'T>>) = 
        async { 
            let! m = self.Receive()
            match m with
            | TestAsync _ -> ()
            | TestSync(rc, _) -> do! rc.Reply ()
        }
    
    let rec consume (self : Actor<TestMessage<unit>>) = 
        async { 
            let! m = self.Receive()
            match m with
            | TestAsync() -> ()
            | TestSync(rc, _) -> do! rc.Reply ()
            return! consume self
        }
    
    let selfStop (self : Actor<TestMessage<unit>>) = 
        async { 
            let! m = self.Receive()
            match m with
            | TestSync(rc, _) -> 
                do! rc.Reply ()
                self.Stop()
            | _ -> self.Stop()
        }
    
    let rec stateful (s : 'S) (self : Actor<TestMessage<'S, 'S>>) = 
        async { 
            let! m = self.Receive()
            match m with
            | TestAsync s' -> return! stateful s' self
            | TestSync(rc, s') -> 
                do! rc.Reply s
                return! stateful s' self
        }

    let rec stateless (self: Actor<TestMessage<'T>>) =
        async {
            let! m = self.Receive()
            match m with
            | TestAsync _ -> return! stateless self
            | TestSync(rc, _) ->
                do! rc.Reply ()
                return! stateless self
        }
    
    let rec failing (self : Actor<TestMessage<'T>>) = 
        async { 
            let! m = self.Receive()
            match m with
            | TestSync(rc, _) -> 
                do! rc.Reply ()
                failwith "Dead sync"
                return! failing self
            | _ -> return! failing self
        }

module Behaviors = 
    let refCell (cell : 'T ref) (m : TestMessage<'T>) = 
        async { 
            match m with
            | TestAsync s -> cell := s
            | TestSync(rc, _) -> do! rc.Reply ()
        }
    
    let state (s : 'S) (m : TestMessage<'S, 'S>) = 
        async { 
            match m with
            | TestAsync s -> return s
            | TestSync(rc, s') -> 
                do! rc.Reply s
                return s'
        }
    
    let stateNoUpdateOnSync (s : 'S) (m : TestMessage<'S, 'S>) = 
        async { 
            match m with
            | TestAsync s -> return s
            | TestSync(rc, _) -> 
                do! rc.Reply s
                return s
        }
    
    let delayedState (s : int) (m : TestMessage<int, int>) = 
        async { 
            match m with
            | TestAsync s -> return s
            | TestSync(rc, t) -> 
                do! Async.Sleep t
                do! rc.Reply s
                return s
        }
    
    let list (l : 'T list) (m : TestList<'T>) = 
        async { 
            match m with
            | ListPrepend v -> return v :: l
            | Delay t -> 
                do! Async.Sleep t
                return l
            | ListGet rc -> 
                do! rc.Reply l
                return l
        }
    
    let adder (i : int) (m : TestMessage<int, int>) = 
        async { 
            match m with
            | TestAsync i' -> return i + i'
            | TestSync(rc, _) -> 
                do! rc.Reply i
                return i
        }
    
    let divider (i : int) (m : TestMessage<int, int>) = 
        async { 
            match m with
            | TestAsync i' -> return i'
            | TestSync(rc, i') -> 
                try 
                    let i'' = i / i'
                    do! rc.Reply i'
                    return i''
                with e -> 
                    do! rc.ReplyWithException e
                    return i
        }
    
    let forward (target : ActorRef<'T>) (m : 'T) = target <-!- m
    
    let multiRepliesForward (target : ActorRef<TestMultiReplies<'T>>) (m : TestMessage<'T, 'T>) = 
        async { 
            match m with
            | TestAsync v -> do! target <-!- MultiRepliesAsync v
            | TestSync(rc, _) -> do! target <!- fun ch -> MultiRepliesSync(ch, rc)
        }
    
    let multiRepliesState (s : 'S) (m : TestMultiReplies<'S>) = 
        async { 
            match m with
            | MultiRepliesAsync v -> return v
            | MultiRepliesSync(rc1, rc2) -> 
                do! rc1.Reply ()
                do! rc2.Reply s
                return s
        }


module Remote = 
    open System.Reflection
    open Nessos.Thespian.Remote
    
    [<AbstractClass>]
    type ActorManager() = 
        inherit MarshalByRefObject()
        abstract Init : unit -> unit
        abstract Fini : unit -> unit
        override __.Init() = ()
        override __.Fini() = ()
    
    [<AbstractClass>]
    type ActorManager<'T>(behavior : Actor<'T> -> Async<unit>, ?name : string) = 
        inherit ActorManager()
        
        [<VolatileField>]
        let mutable actor = 
            Actor.bind behavior |> fun a -> 
                if name.IsSome then Actor.rename name.Value a
                else a
        
        abstract Actor : Actor<'T>
        abstract SetActor : Actor<'T> -> unit
        abstract Ref : ActorRef<'T>
        abstract Publish : unit -> ActorRef<'T>
        abstract Start : unit -> unit
        abstract Stop : unit -> unit
        override __.Actor = actor
        override __.SetActor(actor' : Actor<'T>) = actor <- actor'
        override __.Ref = actor.Ref
        override __.Start() = actor.Start()
        override __.Stop() = actor.Stop()
        override __.Fini() = __.Stop()
    
    type UtcpActorManager<'T>(behavior : Actor<'T> -> Async<unit>, ?name : string) = 
        inherit ActorManager<'T>(behavior, ?name = name)
        override self.Publish() = 
            let actor = self.Actor |> Actor.publish [ Protocols.utcp() ]
            self.SetActor(actor)
            actor.Ref
    
    type BtcpActorManager<'T>(behavior : Actor<'T> -> Async<unit>, ?name : string) = 
        inherit ActorManager<'T>(behavior, ?name = name)
        override self.Publish() = 
            let actor = self.Actor |> Actor.publish [ Protocols.btcp() ]
            self.SetActor(actor)
            actor.Ref
    
    type BehaviorValue<'T> = 
        | Behavior of byte []
        
        static member Create(behavior : Actor<'T> -> Async<unit>) = 
            let serializer = Serialization.defaultSerializer
            Behavior(serializer.Serialize<Actor<'T> -> Async<unit>>(behavior))
        
        member self.Unwrap() = 
            let (Behavior payload) = self
            let serializer = Serialization.defaultSerializer
            serializer.Deserialize<Actor<'T> -> Async<unit>>(payload)
    
    [<AbstractClass>]
    type ActorManagerFactory() = 
        inherit MarshalByRefObject()
        abstract CreateActorManager : (Actor<'T> -> Async<unit>) * ?name:string -> ActorManager<'T>
        abstract Fini : unit -> unit
    
    type UtcpActorManagerFactory() = 
        inherit ActorManagerFactory()
        let mutable managers = []
        
        override __.CreateActorManager(behavior : Actor<'T> -> Async<unit>, ?name : string) = 
            let manager = new UtcpActorManager<'T>(behavior, ?name = name)
            manager.Init()
            managers <- (manager :> ActorManager) :: managers
            manager :> ActorManager<'T>
        
        override __.Fini() = 
            for manager in managers do
                manager.Fini()
    
    type BtcpActorManagerFactory() = 
        inherit ActorManagerFactory()
        let mutable managers = []
        
        override __.CreateActorManager(behavior : Actor<'T> -> Async<unit>, ?name : string) = 
            let manager = new BtcpActorManager<'T>(behavior, ?name = name)
            manager.Init()
            managers <- (manager :> ActorManager) :: managers
            manager :> ActorManager<'T>
        
        override __.Fini() = 
            for manager in managers do
                manager.Fini()
    
    open System.Collections.Generic
    
#if !NETCOREAPP2_2
    type AppDomainPool() = 
        static let appDomains = new Dictionary<string, AppDomain>()
        
        static member CreateDomain(name : string) = 
            //      printfn "Creating domain: %s" name
            let currentDomain = AppDomain.CurrentDomain
            let appDomainSetup = currentDomain.SetupInformation
            let evidence = new Security.Policy.Evidence(currentDomain.Evidence)
            let a = AppDomain.CreateDomain(name, evidence, appDomainSetup)
            appDomains.Add(name, a)
            a
        
        static member GetOrCreate(name : string) = 
            let exists, appdomain = appDomains.TryGetValue(name)
            if exists then appdomain
            else AppDomainPool.CreateDomain(name)
    
    type AppDomainManager<'T when 'T :> ActorManagerFactory>(?appDomainName : string) = 
        let appDomainName = defaultArg appDomainName "testDomain"
        let appDomain = AppDomainPool.GetOrCreate(appDomainName)
        let factory = 
            appDomain.CreateInstance(Assembly.GetExecutingAssembly().FullName, typeof<'T>.FullName).Unwrap() 
            |> unbox<'T>
        member __.Factory = factory
        interface IDisposable with
            member __.Dispose() = factory.Fini()
#endif


module Assert =
    open NUnit.Framework

    let throws<'e when 'e :> exn> (f : unit -> unit) = Assert.Throws<'e>(fun () -> f ()) |> ignore