﻿module Nessos.Thespian.Tests.TestDefinitions

open System
open Nessos.Thespian

type TestMessage<'T, 'R> =
  | TestAsync of 'T
  | TestSync of IReplyChannel<'R> * 'T

type TestMessage<'T> = TestMessage<'T, unit>


module PrimitiveBehaviors =
  let nill (self: Actor<TestMessage<unit>>) = async.Zero()

  let consumeOne (self: Actor<TestMessage<'T>>) =
    async {
      let! m = self.Receive()
      match m with
      | TestAsync _ -> ()
      | TestSync(R reply, _) -> reply <| Value()
    }

  let rec consume (self: Actor<TestMessage<unit>>) =
    async {
      let! m = self.Receive()
      match m with
      | TestAsync() -> ()
      | TestSync(R reply, _) -> reply <| Value()
      return! consume self
    }

  let selfStop (self: Actor<TestMessage<unit>>) =
    async {
      let! m = self.Receive()
      match m with
      | TestSync(R reply, _) -> reply <| Value(); self.Stop()
      | _ -> self.Stop()
    }

  let rec stateful (s: 'S) (self: Actor<TestMessage<'S, 'S>>) =
    async {
      let! m = self.Receive()
      match m with
      | TestAsync s' -> return! stateful s' self
      | TestSync(R reply, s') -> reply (Value s); return! stateful s' self
    }

  let rec failing (self: Actor<TestMessage<'T>>) =
    async {
      let! m = self.Receive()
      match m with
      | TestSync(R reply, _) -> reply nothing; failwith "Dead sync"; return! failing self
      | _ -> return! failing self
    }

module Behaviors =
  let refCell (cell: 'T ref) (m: TestMessage<'T, 'T>) =
    async {
      match m with
      | TestAsync s -> cell := s
      | TestSync(R reply, _) -> reply (Value cell.Value)
    }

  let state (s: 'S) (m: TestMessage<'S, 'S>) =
    async {
      match m with
      | TestAsync s -> return s
      | TestSync(R reply, s') -> reply (Value s); return s'
    }

module Remote =
  open System.Reflection
  open Nessos.Thespian.Remote

  [<AbstractClass>]
  type ActorManager() =
    inherit MarshalByRefObject()
    abstract Init: unit -> unit
    abstract Fini: unit -> unit
    
    default __.Init() = ()
    default __.Fini() = ()

  [<AbstractClass>]
  type ActorManager<'T>(behavior: Actor<'T> -> Async<unit>, ?name: string) =
    inherit ActorManager()
    let actor = Actor.bind behavior |> fun a -> if name.IsSome then Actor.rename name.Value a else a

    abstract Actor: Actor<'T>
    abstract Publish: unit -> ActorRef<'T>
    abstract Start: unit -> unit
    abstract Stop: unit -> unit

    default __.Actor = actor
    default __.Start() = actor.Start()
    default __.Stop() = actor.Stop()


  type UtcpActorManager<'T>(behavior: Actor<'T> -> Async<unit>, ?name: string) =
    inherit ActorManager<'T>(behavior, ?name = name)
    override self.Publish() = self.Actor |> Actor.publish [Protocols.utcp()] |> Actor.ref

  type BtcpActorManager<'T>(behavior: Actor<'T> -> Async<unit>, ?name: string) =
    inherit ActorManager<'T>(behavior, ?name = name)
    override self.Publish() = self.Actor |> Actor.publish [Protocols.btcp()] |> Actor.ref

  [<AbstractClass>]
  type ActorManagerFactory() =
    inherit MarshalByRefObject()
    abstract CreateActorManager: (Actor<'T> -> Async<unit>) * ?name: string -> ActorManager<'T>
    abstract Fini: unit -> unit

  type UtcpActorManagerFactory() =
    inherit ActorManagerFactory()
    let mutable managers = []
    override __.CreateActorManager(behavior: Actor<'T> -> Async<unit>, ?name: string) =
      let manager = new UtcpActorManager<'T>(behavior, ?name = name)
      managers <- (manager :> ActorManager)::managers
      manager :> ActorManager<'T>
    override __.Fini() = for manager in managers do manager.Fini()

  type BtcpActorManagerFactory() =
    inherit ActorManagerFactory()
    let mutable managers = []
    override __.CreateActorManager(behavior: Actor<'T> -> Async<unit>, ?name: string) =
      let manager = new BtcpActorManager<'T>(behavior, ?name = name)
      managers <- (manager :> ActorManager)::managers
      manager :> ActorManager<'T>
    override __.Fini() = for manager in managers do manager.Fini()
  
  type AppDomainManager<'T when 'T :> ActorManagerFactory>(?appDomainName: string) =
    let appDomainName = defaultArg appDomainName "testDomain"
    let appDomain =
      let currentDomain = AppDomain.CurrentDomain
      let appDomainSetup = new AppDomainSetup()
      appDomainSetup.ApplicationBase <- currentDomain.BaseDirectory
      let evidence = new Security.Policy.Evidence(currentDomain.Evidence)
      AppDomain.CreateDomain(appDomainName, evidence, appDomainSetup)

    let factory =
      appDomain.CreateInstance(Assembly.GetExecutingAssembly().FullName, typeof<'T>.FullName).Unwrap() |> unbox<'T>

    member __.Factory = factory

    interface IDisposable with override __.Dispose() = AppDomain.Unload(appDomain)
