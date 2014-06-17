namespace Nessos.Thespian.Tests

open System
open NUnit.Framework
open FsUnit

open Nessos.Thespian
open Nessos.Thespian.Tests.TestDefinitions
open Nessos.Thespian.Tests.TestDefinitions.Remote

[<AbstractClass>]
type ``AppDomain Communication``<'T when 'T :> ActorManagerFactory>() =
  abstract GetAppDomainManager: ?appDomainName: string -> AppDomainManager<'T>

  [<Test>]
  member self.``Post via ref``() =
    use appDomainManager = self.GetAppDomainManager()
    let actorManager = appDomainManager.Factory.CreateActorManager(fun a -> Behavior.stateful 0 Behaviors.state a)
    let actorRef = actorManager.Publish()
    actorManager.Start()

    actorRef <-- TestAsync 42
    let result = actorRef <!= fun ch -> TestSync(ch, 43)
    result |> should equal 42

  [<Test>]
  [<ExpectedException(typeof<TimeoutException>)>]
  [<Timeout(60000)>] //make sure the default timeout is less than the test case timeout
  member self.``Post with reply with no timeout (default timeout)``() =
    use appDomainManager = self.GetAppDomainManager()
    let actorManager = appDomainManager.Factory.CreateActorManager(fun a -> PrimitiveBehaviors.nill a)
    let actorRef = actorManager.Publish()
    actorManager.Start()
    
    actorRef <!= fun ch -> TestSync(ch, ())


  [<Test>]
  [<ExpectedException(typeof<UnknownRecipientException>)>]
  member self.``Post when stopped``() =
    use appDomainManager = self.GetAppDomainManager()
    let actorManager = appDomainManager.Factory.CreateActorManager(fun a -> PrimitiveBehaviors.nill a)
    let actorRef = actorManager.Publish()

    actorRef <-- TestAsync()

  [<Test>]
  [<ExpectedException(typeof<UnknownRecipientException>)>]
  member self.``Post when started, stop and post``() =
    use appDomainManager = self.GetAppDomainManager()
    let actorManager = appDomainManager.Factory.CreateActorManager(fun a -> Behavior.stateful 0 Behaviors.state a)
    let actorRef = actorManager.Publish()
    actorManager.Start()

    actorRef <-- TestAsync 42
    let r = actorRef <!= fun ch -> TestSync(ch, 43)
    r |> should equal 42

    actorManager.Stop()
    actorRef <-- TestAsync 0

