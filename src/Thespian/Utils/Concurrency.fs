namespace Nessos.Thespian.Utilities

    open System.Threading

    /// Thread-safe value container with optimistic update semantics
    type Atom<'T when 'T : not struct>(value : 'T) =
        let refCell = ref value

        let rec swap f = 
            let currentValue = refCell.Value
            let result = Interlocked.CompareExchange<'T>(refCell, f currentValue, currentValue)
            if obj.ReferenceEquals(result, currentValue) then ()
            else Thread.SpinWait 20; swap f

        let transact f =
            let result = ref Unchecked.defaultof<_>
            let f' t = let t',r = f t in result := r ; t'
            swap f' ; result.Value

        /// Get Current Value
        member self.Value : 'T = !refCell

        /// <summary>
        ///     Atomically updates the container.
        /// </summary>
        /// <param name="updateF">updater function.</param>
        member self.Swap (updateF : 'T -> 'T) : unit = swap updateF

        /// <summary>
        ///     Perform atomic transaction on container.
        /// </summary>
        /// <param name="transactionF">transaction function.</param>
        member self.Transact(transactionF : 'T -> 'T * 'R) : 'R = transact transactionF

        /// <summary>
        ///     Force a new value on container.
        /// </summary>
        /// <param name="value">value to be set.</param>
        member self.Force (value : 'T) = refCell := value


    [<RequireQualifiedAccess>]
    module Atom =

        /// <summary>
        ///     Initialize a new atomic container with given value.
        /// </summary>
        /// <param name="value">Initial value.</param>
        let atom<'T when 'T : not struct> value = new Atom<'T>(value)

        /// <summary>
        ///     Atomically updates the container with given function.
        /// </summary>
        /// <param name="atom">Atom to be updated.</param>
        /// <param name="updateF">Updater function.</param>
        let swap (atom : Atom<'T>) updateF = atom.Swap updateF

        /// <summary>
        ///     Perform atomic transaction on container.
        /// </summary>
        /// <param name="atom">Atom to perform transaction on.</param>
        /// <param name="transactF">Transaction function.</param>
        let transact (atom : Atom<'T>) transactF : 'R = atom.Transact transactF
        
        /// <summary>
        ///     Force value on given atom.
        /// </summary>
        /// <param name="atom">Atom to be updated.</param>
        /// <param name="value">Value to be set.</param>
        let force (atom : Atom<'T>) value = atom.Force value


    /// <summary>
    ///     Thread-safe latch
    /// </summary>
    type Latch() =
        let mutable switch = 0
        member __.Trigger() = Interlocked.CompareExchange(&switch, 1, 0) = 0

    /// <summary>
    ///     thread safe counter implementation
    /// </summary>
    type ConcurrentCounter (?start : int64) =
        let mutable count = defaultArg start 0L

        /// <summary>
        ///     Increment the counter
        /// </summary>
        member __.Incr () = Interlocked.Increment &count

        /// <summary>
        ///     Current counter value
        /// </summary>
        member __.Value = count

    /// <summary>
    ///     Thread-safe countdown latch.
    /// </summary>
    type CountdownLatch() =
        [<VolatileField>]
        let mutable counter = 0

        ///Set the latch
        member self.Increment(): unit =
            Interlocked.Increment(&counter) |> ignore

        ///Reset the latch
        member __.Decrement(): unit =
            Interlocked.Decrement(&counter) |> ignore

        ///Spin-wait until the latch is reset
        member __.WaitToZero(): unit =
            while (Interlocked.CompareExchange(&counter, 0, 0) <> 0) do Thread.SpinWait 20
