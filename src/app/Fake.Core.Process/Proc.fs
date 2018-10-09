﻿namespace Fake.Core

open System
open System.IO
open System.Diagnostics
open Fake.Core.ProcessHelpers

[<AutoOpen>]
module StreamExtensions =

    type System.IO.Stream with
        static member CombineWrite (target1:System.IO.Stream, target2:System.IO.Stream)=
            if not target1.CanWrite || not target2.CanWrite then 
                raise <| System.ArgumentException("Streams need to be writeable to combine them.")
            let notsupported () = raise <| System.InvalidOperationException("operation not suppotrted")
            { new System.IO.Stream() with
                member __.CanRead = false
                member __.CanSeek = false
                member __.CanTimeout = target1.CanTimeout || target2.CanTimeout
                member __.CanWrite = true
                member __.Length = target1.Length
                member __.Position with get () = target1.Position and set _ = notsupported()
                member __.Flush () = target1.Flush(); target2.Flush()
                member __.FlushAsync (tok) = 
                    async {
                        do! target1.FlushAsync(tok)
                        do! target2.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member __.Seek (_, _) = notsupported()
                member __.SetLength (_) = notsupported()
                member __.Read (_, _, _) = notsupported()
                member __.Write (buffer, offset, count)=
                    target1.Write(buffer, offset, count)
                    target2.Write(buffer, offset, count)
                override __.WriteAsync(buffer, offset, count, tok) =
                    async {
                        let! child1 = 
                            target1.WriteAsync(buffer, offset, count, tok)
                            |> Async.AwaitTask
                            |> Async.StartChild
                        let! child2 =
                            target2.WriteAsync(buffer, offset, count, tok)
                            |> Async.AwaitTask
                            |> Async.StartChild
                        do! child1
                        do! child2
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                }

        static member InterceptStream (readStream:System.IO.Stream, track:System.IO.Stream)=
            if not readStream.CanRead || not track.CanWrite then 
                raise <| System.ArgumentException("track Stream need to be writeable and readStream readable to intercept the readStream.")
            { new System.IO.Stream() with
                member __.CanRead = true
                member __.CanSeek = readStream.CanSeek
                member __.CanTimeout = readStream.CanTimeout || track.CanTimeout
                member __.CanWrite = readStream.CanWrite
                member __.Length = readStream.Length
                member __.Position with get () = readStream.Position and set v = readStream.Position <- v
                member __.Flush () = readStream.Flush(); track.Flush()
                member __.FlushAsync (tok) = 
                    async {
                        do! readStream.FlushAsync(tok)
                        do! track.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member __.Seek (offset, origin) = readStream.Seek(offset, origin)
                member __.SetLength (l) = readStream.SetLength(l)
                member __.Read (buffer, offset, count) =
                    let read = readStream.Read(buffer, offset, count)
                    track.Write(buffer, offset, read)
                    read
                override __.ReadAsync (buffer, offset, count, _) =
                  async {
                    let! read = readStream.ReadAsync(buffer, offset, count)
                    do! track.WriteAsync(buffer, offset, read)
                    return read
                  }
                  |> Async.StartImmediateAsTask
                member __.Write (buffer, offset, count)=
                    readStream.Write(buffer, offset, count)
                override __.WriteAsync(buffer, offset, count, tok) =
                    readStream.WriteAsync(buffer, offset, count, tok)
                override __.Dispose(t) = if t then readStream.Dispose()
                }


module internal InternalStreams =
    open System
    open System.Threading
    open System.Collections
    open System.Collections.Generic
    module AsyncHelper = 
        let FromBeginEndCancel beginAction endAction cancelAction =
            let asyncResult = ref null
            Async.FromBeginEnd(
                (fun (callback, state) -> 
                    asyncResult := beginAction(callback, state)
                    !asyncResult), 
                (fun res -> 
                    endAction res), 
                cancelAction  = (fun () -> 
                    while !asyncResult = null do Thread.Sleep 20
                    cancelAction(!asyncResult)))
    open AsyncHelper

    type ConcurrentQueueMessage<'a> =
        | Enqueue of 'a * AsyncReplyChannel<exn option>
        | Dequeue of AsyncReplyChannel<Choice<'a, exn>>
        | TryDequeue of AsyncReplyChannel<Choice<'a option,exn>>

    type ConcurrentQueue<'a>() =
        let core =
            let queue = Queue<'a>()
            let waitingQueue = Queue<AsyncReplyChannel<Choice<'a, exn>>>()
            MailboxProcessor.Start(fun inbox ->            
                let rec loop () = async {
                    let! msg = inbox.Receive()                
                    match msg with
                    | Enqueue (item,reply) ->
                        try
                            if waitingQueue.Count > 0 then
                                let waiting = waitingQueue.Dequeue()
                                waiting.Reply (Choice1Of2 item)
                            else
                                queue.Enqueue item
                            reply.Reply None
                        with exn ->
                            reply.Reply (Some exn)
                    | Dequeue reply ->
                        try
                            if queue.Count > 0 then
                                let item = queue.Dequeue()
                                reply.Reply (Choice1Of2 item)
                            else
                                waitingQueue.Enqueue reply
                        with exn ->
                            reply.Reply (Choice2Of2 exn)
                    | TryDequeue reply ->
                        try
                            let item = 
                                if queue.Count > 0 then
                                    Some <| queue.Dequeue()
                                else None
                            reply.Reply (Choice1Of2 item)
                        with exn ->
                            reply.Reply (Choice2Of2 exn)
                    return! loop() }
                loop())
        member x.EnqueueAsync(item) = async {
            let! item = core.PostAndAsyncReply(fun reply -> Enqueue (item, reply))
            return
                match item with
                | Some exn -> raise exn
                | None -> () }
        member x.DequeAsyncTimeout(?timeout) = async {
            let! result = 
                core.PostAndTryAsyncReply(
                    (fun reply -> Dequeue (reply)), ?timeout = timeout)
            return
                match result with
                | Some r ->
                    match r with
                    | Choice1Of2 item -> Some item
                    | Choice2Of2 exn -> raise exn
                | None -> None }
        member x.DequeAsync() = async {
            let! result = 
                core.PostAndAsyncReply(
                    (fun reply -> Dequeue (reply)))
            return
                match result with
                | Choice1Of2 item -> item
                | Choice2Of2 exn -> raise exn }
                
        member x.TryDequeAsync() = async {
            let! result = core.PostAndAsyncReply(fun reply -> TryDequeue (reply))
            return
                match result with
                | Choice1Of2 item -> item
                | Choice2Of2 exn -> raise exn }
        member x.Enqueue(item) = x.EnqueueAsync item |> Async.RunSynchronously
        member x.Deque() = x.DequeAsync () |> Async.RunSynchronously
        member x.TryDeque() = x.TryDequeAsync () |> Async.RunSynchronously
    exception ReadCanceledException
    type MyIAsyncReadResult<'a> (callback:AsyncCallback, state) = 
        let event = new AutoResetEvent(false)
        let mutable completed = false
        let mutable canceled = false
        let mutable data = None
        let syncRoot = obj()
        
        interface IAsyncResult with
            member x.AsyncState with get() = state
            member x.IsCompleted with get() = completed
            member x.AsyncWaitHandle with get() = event :> WaitHandle
            member x.CompletedSynchronously with get() = false
        member x.End(resultData:option<'a>) =
            lock syncRoot (fun () ->
                if canceled then
                    raise ReadCanceledException
                data <- resultData
                event.Set() |> ignore
                if callback <> null then
                    callback.Invoke (x:>IAsyncResult)
                completed <- true)
        member x.Read 
            with get () = 
                data
        member x.Cancel() = 
            lock syncRoot (fun () ->
                if completed then
                    failwith "operation already completed!"
                canceled <- true)
        member x.IsCanceled with get() = canceled
        
    type IStream<'a> =
        inherit IDisposable
        abstract member Read : unit -> Async<'a option>
        abstract member Write : 'a -> Async<unit>
    type AsyncStreamHelper<'a> (innerStream:IStream<'a>) = 
        let queue = ConcurrentQueue<MyIAsyncReadResult<'a>>() 
        let workerCts = new System.Threading.CancellationTokenSource()
        let worker = 
            Async.StartAsTask (async {
                let! (cts:CancellationToken) = Async.CancellationToken
                while not cts.IsCancellationRequested do
                    let! data = innerStream.Read()
                    let finished = ref false
                    while not !finished do
                        let! (asyncResult:MyIAsyncReadResult<'a>) = queue.DequeAsync()
                        if not asyncResult.IsCanceled then
                            try
                                asyncResult.End(data)
                                finished := true
                            with ReadCanceledException -> () // find next
                return ()
            }, cancellationToken = workerCts.Token)
                
           
        let beginRead(callback, state) = 
            let result = new MyIAsyncReadResult<'a>(callback, state)
            queue.Enqueue result
            result :> IAsyncResult

        let endRead(asyncResult:IAsyncResult) = 
            let readResult = asyncResult :?> MyIAsyncReadResult<'a>
            if asyncResult.IsCompleted then
                readResult.Read
            else
                // block for exit
                WaitHandle.WaitAll ([|asyncResult.AsyncWaitHandle|]) |> ignore
                readResult.Read

        let cancelRead(asyncResult:IAsyncResult) =
            let readResult = asyncResult :?> MyIAsyncReadResult<'a>
            readResult.Cancel()

        let read ()  = 
            AsyncHelper.FromBeginEndCancel beginRead endRead cancelRead

        interface IStream<'a> with
            member x.Read() = read()
            member x.Write d = innerStream.Write(d)
            member x.Dispose () = innerStream.Dispose()
        member x.BaseStream with get() = innerStream
        static member FromAdvancedRead advancedRead count = 
            async {
                let buffer = Array.zeroCreate count
                let! read = advancedRead(buffer, 0, count)
                return Array.sub buffer 0 read
            }

    module StreamModule =
        let createUnsupported() =
            { new IStream<'a> with
                member x.Dispose () = ()
                member x.Read () = raise <| System.NotSupportedException ""
                member x.Write input = raise <| System.NotSupportedException "" }

        type StreamHelper(istream:IStream<byte array>) =
            inherit Stream()
            let mutable cache = [||]
            let mutable currentIndex = 0
            let mutable isDisposed = false
            let read (dst:byte array) offset count = async {
                let! newCache = 
                    if cache.Length - currentIndex > 0 then
                        async.Return cache
                    else
                      async {
                        currentIndex <- 0
                        let! data = istream.Read()
                        return
                            match data with
                            | Some d -> d
                            | None -> [||] }
                cache <- newCache
                // Use cache
                let realCount = 
                    Math.Min(
                        cache.Length - currentIndex,
                        count)
                Array.Copy(cache, currentIndex, dst, offset, realCount)
                currentIndex <- currentIndex + realCount
                return realCount }
            let write dst offset count = async {
                if count > 0 then
                    let newDst =
                         Array.sub dst offset count
                    return! istream.Write newDst }
            let readOne () = async {
                let dst = Array.zeroCreate 1
                let! result = read dst 0 1
                return
                    if result = 0 then
                        None
                    else Some (dst.[0]) }
                
            let writeOne b = istream.Write [|b|]
            let beginRead, endRead, cancelRead = 
                Async.AsBeginEnd(fun (dst, offset, count) -> read dst offset count)
            let beginWrite, endWrite, cancelWrite = 
                Async.AsBeginEnd(fun (src, offset, count) -> write src offset count)
                
            let checkDisposed() =
                if isDisposed then
                    raise <| ObjectDisposedException("onetimestream")
            override x.ReadAsync (dst, offset, count, tok) = 
                Async.StartAsTask(read dst offset count, cancellationToken = tok)
            override x.WriteAsync (dst, offset, count, tok) = 
                Async.StartAsTask(write dst offset count, cancellationToken = tok)
                :> System.Threading.Tasks.Task
            override x.Flush () = ()        
            override x.Seek(offset:int64, origin:SeekOrigin) =
                raise <| NotSupportedException()        
            override x.SetLength(value:int64) =
                raise <| NotSupportedException()        
            //override x.BeginRead(dst, offset, count, callback, state) =
            //    beginRead((dst, offset, count), callback, state)        
            //override x.EndRead(res) =
            //    endRead res
            member x.CancelRead(res) = 
                cancelRead(res)
            //override x.BeginWrite(src, offset, count, callback, state) =
            //    beginWrite((src, offset, count), callback, state)
            //override x.EndWrite(res) =
            //    endWrite res
            member x.CancelWrite(res) = 
                cancelWrite(res)
            override x.Read(dst, offset, count) = 
                read dst offset count |> Async.RunSynchronously            
            override x.Write(src, offset, count) = 
                write src offset count |> Async.RunSynchronously
            override x.ReadByte() =
                if isDisposed then -1
                else
                    match readOne() |> Async.RunSynchronously with
                    | Some s -> int s
                    | None -> -1
            override x.WriteByte item =
                if not isDisposed then
                    writeOne item |> Async.RunSynchronously        
            override x.CanRead 
                with get() = 
                    checkDisposed()
                    true        
            override x.CanSeek
                with get() =
                    checkDisposed()
                    false                
            override x.CanWrite
                with get() =
                    checkDisposed()
                    true        
            override x.Length
                with get() =
                    raise <| NotSupportedException()                
            override x.Position
                with get() =
                    raise <| NotSupportedException()
                and set value =            
                    raise <| NotSupportedException()             
            override x.Dispose disposing =
                if not isDisposed then
                    isDisposed <- true
                    if disposing then
                        istream.Dispose()
                base.Dispose disposing
                    
        let fromInterface istream = new StreamHelper(istream) :> Stream
        [<AutoOpen>]
        module StreamExtensions = 
            type System.IO.Stream with
                //member s.MyReadAsync(buffer:byte array,offset,count) =   
                //    match s with
                //    | :? StreamHelper as helper -> helper.ReadAsync(buffer, offset, count)
                //    | _ ->
                //        Async.FromBeginEnd(
                //            (fun (callback, state) -> s.BeginRead(buffer, offset, count, callback, state)),
                //            (fun result -> s.EndRead result))
            
                //member s.MyWriteAsync(buffer:byte array,offset,count) =
                //    match s with
                //    | :? StreamHelper as helper -> helper.WriteAsync(buffer, offset, count)
                //    | _ ->
                //        Async.FromBeginEnd(
                //            (fun (callback, state) -> s.BeginWrite(buffer, offset, count, callback, state)),
                //            (fun result -> s.EndWrite result))
                member s.AsyncRead c = AsyncStreamHelper<_>.FromAdvancedRead (s.ReadAsync >> Async.AwaitTask) c
                member s.AsyncRead (buffer, offset, count) = Async.AwaitTask(s.ReadAsync(buffer, offset, count))
                member s.AsyncWrite (buffer, offset, count) = Async.AwaitTask(s.WriteAsync(buffer, offset, count))
            type IStream<'a> with
                member s.ReadWait() = 
                    s.Read() |> Async.RunSynchronously
                member s.WriteWait(d) = 
                    s.Write(d) |> Async.RunSynchronously

        open StreamExtensions
        let toCancelAbleStream s = new AsyncStreamHelper<_>(s) :> IStream<_>
        let fromReadWriteDispose dis read write = 
            { new IStream<_> with
                member x.Read () =
                    read()
                member x.Write item = 
                    write item
              interface IDisposable with
                member x.Dispose () = dis () } |> toCancelAbleStream
        let fromReadWrite read write = fromReadWriteDispose id read write
        open StreamExtensions
        let toInterface buffersize (stream:System.IO.Stream) = 
            let buffer = Array.zeroCreate buffersize
            let read () = async {
                let! read = stream.ReadAsync(buffer, 0, buffer.Length)
                let readData =
                    Array.sub buffer 0 read
                return
                    if readData.Length > 0 then
                        Some readData
                    else None }
            let write (src:byte array) = async {
                do! stream.AsyncWrite(src, 0, src.Length)
                stream.Flush()
                }
            let dispose () = 
                // BUG: Make all AsyncRead calls end!
                stream.Flush()
                //stream.Close()
                stream.Dispose()
            fromReadWriteDispose dispose read write
        let toMaybeRead read () = async {
            let! data = read()
            return Some data }
            
            
        let infiniteStream () = 
            let queue = new ConcurrentQueue<'a>()
            fromReadWrite (toMaybeRead queue.DequeAsync) queue.EnqueueAsync

        let toLimitedStream (raw:IStream<_>) = 
            //let raw = infiniteStream()
            let readFinished = ref false
            let read () =
                if !readFinished then
                   async.Return None
                else 
                  async {
                    let! data = raw.Read()
                    return
                        match data with
                        | Some s -> 
                            match s with
                            | Some d -> Some d
                            | None -> 
                                readFinished := true
                                None
                        | None -> 
                            failwith "stream should not be limited as we are using an infiniteStream!" }
            let isFinished = ref false
            let finish () = async {
                do! raw.Write None
                isFinished := true }
            let write item = 
                if !isFinished then
                    failwith "stream is in finished state so it should not be written to!"
                raw.Write (Some item)
            finish,
            fromReadWriteDispose raw.Dispose read write
        let limitedStream () = toLimitedStream (infiniteStream())

        let createWriteOnlyPart onDispose (s:IStream<'a>) =
            { new IStream<'a> with
                member x.Dispose () = onDispose()
                member x.Read () = raise <| System.NotSupportedException "Read is not supported"
                member x.Write input = s.Write input }

        let buffer (stream:IStream<_>) =        
            let queue = infiniteStream()
            let write item = async {            
                do! queue.Write item
                do! stream.Write item }
            fromReadWrite queue.Read (fun item -> invalidOp "Write is not allowed"),
            fromReadWriteDispose stream.Dispose stream.Read write

        let combineReadAndWrite (s1:IStream<_>) (s2:IStream<_>) = 
            fromReadWrite s1.Read s2.Write
        let appendFront data (s:IStream<_>) = 
            let first = ref true
            let read () = 
                if !first then
                    first := false
                    async.Return (Some data)
                else
                    s.Read()
            fromReadWriteDispose s.Dispose read s.Write

        let crossStream (s1:IStream<_>) (s2:IStream<_>) = 
            combineReadAndWrite s1 s2,
            combineReadAndWrite s2 s1
        let map f g (s:IStream<_>) = 
            let read () =  async {
                let! read = s.Read()
                return f read }
            let write item = s.Write (g item)
            fromReadWriteDispose s.Dispose read write
        
        let filterRead f (s:IStream<_>) =         
            let rec read () = async {
                let! data = s.Read()
                return!
                    if f data then
                        async.Return data
                    else
                        read () }
            fromReadWriteDispose s.Dispose read s.Write

        let filterWrite f (s:IStream<_>) = 
            let write item = 
                if f item then
                    s.Write (item)
                else async.Return ()
            fromReadWriteDispose s.Dispose s.Read write

        /// Duplicates the given stream, which means returning two stream instances
        /// which will read the same data. 
        /// At the same time buffers all data (ie read from s as fast as possible).
        /// Any data written to the returned instances will be written to the given instance.
        let duplicate (s:IStream<_>) = 
            let close1, s1 = limitedStream()
            let close2, s2 = limitedStream()
            
            let closed = ref false
            async {
                while not !closed do
                    let! data = s.Read()
                    match data with
                    | Some item ->
                        do! s1.Write item
                        do! s2.Write item
                    | None ->
                        do! close1()
                        do! close2() 
                        closed := true } |> Async.Start
            combineReadAndWrite s1 s,
            combineReadAndWrite s2 s

        let split f s = 
            let s1, s2 = duplicate s
            s1 |> filterRead f,
            s2 |> filterRead (not << f)
        //let toSeq (s:IStream<_>) = 
        //  asyncSeq {
        //    while true do
        //        let! data = s.Read()
        //        yield data }
        //let ofSeq write (s:AsyncSeq<_>) = 
        //    let current = ref s
        //    let read () = async {
        //        let! next = !current
        //        return
        //            match next with
        //            | Nil -> failwith "end of sequence"
        //            | Cons(item, next) ->
        //                current := next
        //                item }            
        //    fromReadWrite read write

        let redirect bufferLen (toStream:IStream<_>) (fromStream:IStream<_>) = 
            let closeRead = ref false 
            let cts = new System.Threading.CancellationTokenSource()
            let ev = new ManualResetEvent(false)
            let regularFinish = new ManualResetEvent(false)
            let redirectRun =
                async {
                    do! Async.SwitchToThreadPool()
                    try
                        let buffer = Array.zeroCreate bufferLen
                        let streamFinished = ref false
                        while not !closeRead do
                            let! (read:Option<_>) = fromStream.Read()
                            closeRead :=
                                match read with
                                | Some s -> false
                                | None -> 
                                    streamFinished := true
                                    true

                            if read.IsSome then
                                do! toStream.Write(read.Value)                 
                        toStream.Dispose()
                        if !streamFinished then
                            fromStream.Dispose()
                        regularFinish.Set() |> ignore
                    finally
                        ev.Set() |> ignore
                } 
            let t = Async.StartAsTask(redirectRun, cancellationToken = cts.Token)
            let nT = 
                t.ContinueWith(
                    new Action<Tasks.Task<unit>>(fun t ->
                        ev.Set() |> ignore))
            
            let closeRedirect (timeout:int) waitFinish =             
                let regularFinished = 
                    if waitFinish then
                        // BUG: Reset the timeout when we are still doing something
                        System.Threading.WaitHandle.WaitAll([|regularFinish :> WaitHandle|], timeout)
                    else false
                if not regularFinished then
                    closeRead := true
                    cts.Cancel()
                    ManualResetEvent.WaitAll [|ev|] |> ignore

            closeRedirect
        
        let defaultInput, defaultOutput, defaultError = Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError()
        
        let getStandardInput = 
            let iStream = toInterface 1024 defaultInput
            let modified = iStream |> fromInterface
            Console.SetIn(new StreamReader(modified))
            fun () -> iStream
        
        let getStandardOutput = 
            let istream = defaultOutput |> toInterface 1024
            fun () -> istream
        let getStandardError = 
            let istream = defaultError |> toInterface 1024
            fun () -> istream

/// Hook for events when an CreateProcess is executed.
type internal IProcessHook<'TRes> =
    abstract member PrepareState : unit -> IDisposable
    abstract member PrepareStreams : IDisposable * StreamSpecs -> StreamSpecs
    abstract member ProcessStarted : IDisposable * System.Diagnostics.Process -> unit
    abstract member RetrieveResult : IDisposable * System.Threading.Tasks.Task<RawProcessResult> -> Async<'TRes>

type internal IProcessHookImpl<'TState, 'TRes when 'TState :> IDisposable> =
    abstract member PrepareState : unit -> 'TState
    abstract member PrepareStreams : 'TState * StreamSpecs -> StreamSpecs
    abstract member ProcessStarted : 'TState * System.Diagnostics.Process -> unit
    abstract member RetrieveResult : 'TState * System.Threading.Tasks.Task<RawProcessResult> -> Async<'TRes>

module internal ProcessHook =
    let toRawHook (h:IProcessHookImpl<'TState,'TRes>) =
        { new IProcessHook<'TRes> with
            member x.PrepareState () = 
                let state = h.PrepareState ()
                state :> IDisposable
            member x.PrepareStreams (state, specs) = 
                h.PrepareStreams (state :?> 'TState, specs)
            member x.ProcessStarted (state, proc) =
                h.ProcessStarted (state :?> 'TState, proc)
            member x.RetrieveResult (state, exitCode) =
                h.RetrieveResult (state :?> 'TState, exitCode) }


/// The output of the process. If ordering between stdout and stderr is important you need to use streams.
type ProcessOutput = { Output : string; Error : string }

type ProcessResult<'a> = { Result : 'a; ExitCode : int }

/// Generator for results
//type ResultGenerator<'TRes> =
//    {   GetRawOutput : unit -> ProcessOutput
//        GetResult : ProcessOutput -> 'TRes }
/// Handle for creating a process and returning potential results.
type CreateProcess<'TRes> =
    internal {
        Command : Command
        WorkingDirectory : string option
        Environment : EnvMap option
        Streams : StreamSpecs
        Hook : IProcessHook<'TRes>
    }

    //member x.OutputRedirected = x.HasRedirect 
    member x.CommandLine = x.Command.CommandLine


/// Module for creating and modifying CreateProcess<'TRes> instances
module CreateProcess  =
    let internal emptyHook =
        { new IProcessHook<ProcessResult<unit>> with
            member __.PrepareState () = null
            member __.PrepareStreams (_, specs) = specs
            member __.ProcessStarted (_,_) = ()
            member __.RetrieveResult (_, t) =
                async {
                    let! raw = Async.AwaitTask t
                    return { ExitCode = raw.RawExitCode; Result = () } 
                } }

    (*let internal ofProc (x:RawCreateProcess) =
      { Command = x.Command
        WorkingDirectory = x.WorkingDirectory
        Environment = x.Environment
        Streams = x.Streams
        Hook =
            { new IProcessHook<int> with
                member __.PrepareStart specs = x.OutputHook.Prepare specs
                member __.ProcessStarted (state, p) = x.OutputHook.OnStart(state, p)
                member __.RetrieveResult (s, t) = 
                    x.OutputHook.Retrieve(s, t) } }*)

    let fromCommand command =
        {   Command = command
            WorkingDirectory = None
            // Problem: Environment not allowed when using ShellCommand
            Environment = None
            Streams =
                { // Problem: Redirection not allowed when using ShellCommand
                  StandardInput = Inherit
                  // Problem: Redirection not allowed when using ShellCommand
                  StandardOutput = Inherit
                  // Problem: Redirection not allowed when using ShellCommand
                  StandardError = Inherit }
            Hook = emptyHook }
    let fromRawWindowsCommandLine command windowsCommandLine =
        fromCommand <| RawCommand(command, Arguments.OfWindowsCommandLine windowsCommandLine)
    let fromRawCommand command args =
        fromCommand <| RawCommand(command, Arguments.OfArgs args)

    let ofStartInfo (p:System.Diagnostics.ProcessStartInfo) =
        {   Command = if p.UseShellExecute then ShellCommand p.FileName else RawCommand(p.FileName, Arguments.OfStartInfo p.Arguments)
            WorkingDirectory = if System.String.IsNullOrWhiteSpace p.WorkingDirectory then None else Some p.WorkingDirectory
            Environment = 
                p.Environment
                |> Seq.map (fun kv -> kv.Key, kv.Value)
                |> EnvMap.ofSeq
                |> Some
            Streams =
                {   StandardInput = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
                    StandardOutput = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
                    StandardError = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
                }
            Hook = emptyHook
        }
    let internal interceptStreamFallback onInherit target (s:StreamSpecification) =
        match s with
        | Inherit -> onInherit()
        | UseStream (close, stream) ->
            let combined = Stream.CombineWrite(stream, target)
            UseStream(close, combined)
        | CreatePipe pipe ->
            CreatePipe (StreamRef.Map (fun s -> Stream.InterceptStream(s, target)) pipe)
    let interceptStream target (s:StreamSpecification) =
        interceptStreamFallback (fun _ -> Inherit) target s
    
    let copyRedirectedProcessOutputsToStandardOutputs (c:CreateProcess<_>)=
        { c with
            Streams =
                { c.Streams with
                    StandardOutput =
                        let stdOut = System.Console.OpenStandardOutput()
                        interceptStream stdOut c.Streams.StandardOutput
                    StandardError =
                        let stdErr = System.Console.OpenStandardError()
                        interceptStream stdErr c.Streams.StandardError } }
    
    let withWorkingDirectory workDir (c:CreateProcess<_>)=
        { c with
            WorkingDirectory = Some workDir }
    let withCommand command (c:CreateProcess<_>)=
        { c with
            Command = command }

    let replaceFilePath newFilePath (c:CreateProcess<_>)=
        { c with
            Command =
                match c.Command with
                | ShellCommand s -> failwith "Expected RawCommand"
                | RawCommand (_, c) -> RawCommand(newFilePath, c) }
    let mapFilePath f (c:CreateProcess<_>)=
        c
        |> replaceFilePath (f (match c.Command with ShellCommand s -> failwith "Expected RawCommand" | RawCommand (file, _) -> f file))


    let internal withHook h (c:CreateProcess<_>) =
      { Command = c.Command
        WorkingDirectory = c.WorkingDirectory
        Environment = c.Environment
        Streams = c.Streams
        Hook = h }
    let internal withHookImpl h (c:CreateProcess<_>) =
        c
        |> withHook (h |> ProcessHook.toRawHook)

    let internal simpleHook prepareState prepareStreams onStart onResult =
        { new IProcessHookImpl<_, _> with
            member __.PrepareState () =
                prepareState ()
            member __.PrepareStreams (state, streams) =
                prepareStreams state streams
            member __.ProcessStarted (state, p) = 
                onStart state p
            member __.RetrieveResult (state, exitCode) = 
                onResult state exitCode }

    type internal CombinedState<'a when 'a :> IDisposable > =
        { State1 : IDisposable; State2 : 'a }
        interface IDisposable with
            member x.Dispose() =
                if not (isNull x.State1) then
                    x.State1.Dispose()
                x.State2.Dispose()
    let internal hookAppendFuncs prepareState prepareStreams onStart onResult (c:IProcessHook<'TRes>) =    
        { new IProcessHookImpl<_, _> with
            member __.PrepareState () =
                let state1 = c.PrepareState ()
                let state2 = prepareState ()
                { State1 = state1; State2 = state2 }
            member __.PrepareStreams (state, streams) =
                let newStreams = c.PrepareStreams(state.State1, streams)
                let finalStreams = prepareStreams state.State2 newStreams
                finalStreams
            member __.ProcessStarted (state, p) =
                c.ProcessStarted (state.State1, p)
                onStart state.State2 p
            member __.RetrieveResult (state, exitCode) = 
                async {
                    let d = c.RetrieveResult(state.State1, exitCode)
                    return! onResult d state.State2 exitCode
                } }

    let internal appendFuncs prepareState prepareStreams onStart onResult (c:CreateProcess<_>) =
        c
        |> withHookImpl (
            c.Hook
            |> hookAppendFuncs prepareState prepareStreams onStart onResult
        )

    type internal DisposableWrapper<'a> =
        { State: 'a; OnDispose : 'a -> unit }
        interface IDisposable with
            member x.Dispose () = x.OnDispose x.State
        
    let internal appendFuncsDispose prepareState prepareStreams onStart onResult onDispose (c:CreateProcess<_>) =
        c
        |> appendFuncs
            (fun () ->
                let state = prepareState ()
                { State = state; OnDispose = onDispose })
            (fun state streams -> prepareStreams state.State streams)
            (fun state p -> onStart state.State p)
            (fun prev state exitCode -> onResult prev state.State exitCode)   
    let appendSimpleFuncs prepareState onStart onResult onDispose (c:CreateProcess<_>) =
        c
        |> appendFuncsDispose
            prepareState
            (fun state streams -> streams)
            onStart
            onResult
            onDispose                     

    let addOnSetup f (c:CreateProcess<_>) =
        c
        |> appendSimpleFuncs 
            (fun _ -> f())
            (fun state p -> ())
            (fun prev state exitCode -> prev)
            (fun _ -> ())
    let addOnFinally f (c:CreateProcess<_>) =
        c
        |> appendSimpleFuncs 
            (fun _ -> ())
            (fun state p -> ())
            (fun prev state exitCode -> prev)
            (fun _ -> f ())
    let addOnStarted f (c:CreateProcess<_>) =
        c
        |> appendSimpleFuncs 
            (fun _ -> ())
            (fun state p -> f ())
            (fun prev state exitCode -> prev)
            (fun _ -> ())

    let withEnvironment (env: (string * string) list) (c:CreateProcess<_>)=
        { c with
            Environment = Some (EnvMap.ofSeq env) }
            
    let withEnvironmentMap (env: EnvMap) (c:CreateProcess<_>)=
        { c with
            Environment = Some env }
    let getEnvironmentMap (c:CreateProcess<_>)=
        match c.Environment with
        | Some en -> en
        | None -> EnvMap.create()

    let setEnvironmentVariable envKey (envVar:string) (c:CreateProcess<_>) =
        { c with
            Environment =
                getEnvironmentMap c
                |> IMap.add envKey envVar
                |> Some }

    let withStandardOutput stdOut (c:CreateProcess<_>)=
        { c with
            Streams =
                { c.Streams with
                    StandardOutput = stdOut } }
    let withStandardError stdErr (c:CreateProcess<_>)=
        { c with
            Streams =
                { c.Streams with
                    StandardError = stdErr } }
    let withStandardInput stdIn (c:CreateProcess<_>)=
        { c with
            Streams =
                { c.Streams with
                    StandardInput = stdIn } }

    let map f c =
        c
        |> appendSimpleFuncs 
            (fun _ -> ())
            (fun state p -> ())
            (fun prev state exitCode -> 
                async {
                    let! old = prev
                    return f old
                })
            (fun _ -> ())
    
    let mapResult f (c:CreateProcess<ProcessResult<_>>) =
        c
        |> map (fun r ->
            { ExitCode = r.ExitCode; Result = f r.Result })
    let redirectOutput (c:CreateProcess<_>) =
        c
        |> appendFuncsDispose 
            (fun streams ->
                let outMem = new MemoryStream()
                let errMem = new MemoryStream()
                outMem, errMem)
            (fun (outMem, errMem) streams ->
                { streams with
                    StandardOutput =
                        interceptStreamFallback (fun _ -> UseStream (false, outMem)) outMem streams.StandardOutput
                    StandardError =
                        interceptStreamFallback (fun _ -> UseStream (false, errMem)) outMem streams.StandardError
                })
            (fun (outMem, errMem) p -> ())
            (fun prev (outMem, errMem) exitCode ->
                async {
                    let! prevResult = prev
                    let! exitCode = exitCode |> Async.AwaitTask
                    outMem.Position <- 0L
                    errMem.Position <- 0L
                    let stdErr = (new StreamReader(errMem)).ReadToEnd()
                    let stdOut = (new StreamReader(outMem)).ReadToEnd()
                    let r = { Output = stdOut; Error = stdErr }
                    return { ExitCode = exitCode.RawExitCode; Result = r }
                })
            (fun (outMem, errMem) ->
                outMem.Dispose()
                errMem.Dispose())
  
    let withOutputEvents onStdOut onStdErr (c:CreateProcess<_>) =
        let watchStream onF (stream:System.IO.Stream) =
            async {
                let reader = new System.IO.StreamReader(stream)
                let mutable finished = false
                while not finished do
                    let! line = reader.ReadLineAsync()
                    finished <- isNull line
                    onF line
            }
            |> fun a -> Async.StartImmediateAsTask(a)
        c
        |> appendFuncsDispose
            (fun () ->
                let closeOut, outMem = InternalStreams.StreamModule.limitedStream()
                let closeErr, errMem = InternalStreams.StreamModule.limitedStream()
                
                let outMemS = InternalStreams.StreamModule.fromInterface outMem
                let errMemS = InternalStreams.StreamModule.fromInterface errMem
                let tOut = watchStream onStdOut outMemS
                let tErr = watchStream onStdErr errMemS
                (closeOut, outMem, closeErr, errMem, tOut, tErr))
            (fun (closeOut, outMem, closeErr, errMem, tOut, tErr) streams ->
                { streams with
                    StandardOutput =
                        outMem
                        |> InternalStreams.StreamModule.createWriteOnlyPart (fun () -> closeOut() |> Async.RunSynchronously)
                        |> InternalStreams.StreamModule.fromInterface
                        |> fun s -> interceptStream s streams.StandardOutput
                    StandardError =
                        errMem
                        |> InternalStreams.StreamModule.createWriteOnlyPart (fun () -> closeErr() |> Async.RunSynchronously)
                        |> InternalStreams.StreamModule.fromInterface
                        |> fun s -> interceptStream s streams.StandardError 
                })
            (fun state p -> ())
            (fun prev (closeOut, outMem, closeErr, errMem, tOut, tErr) exitCode ->
                async {
                    let! prevResult = prev
                    do! closeOut()
                    do! closeErr()
                    do! tOut
                    do! tErr
                    return prevResult
                })
            (fun (closeOut, outMem, closeErr, errMem, tOut, tErr) ->
                
                outMem.Dispose()
                errMem.Dispose())
    
    let addOnExited f (c:CreateProcess<_>) =
        c
        |> appendSimpleFuncs 
            (fun _ -> ())
            (fun state p -> ())
            (fun prev state exitCode -> 
                async {
                    let! prevResult = prev
                    let! e = exitCode
                    let s = f prevResult e.RawExitCode
                    return s
                })
            (fun _ -> ())
        
    let ensureExitCodeWithMessage msg (r:CreateProcess<_>) =
        r
        |> addOnExited (fun data exitCode ->
            if exitCode <> 0 then failwith msg
            else data)
            

    let internal tryGetOutput (data:obj) =
        match data with
        | :? ProcessResult<ProcessOutput> as output ->
            Some output.Result
        | :? ProcessOutput as output ->
            Some output
        | _ -> None
        
    let ensureExitCode (r:CreateProcess<_>) =
        r
        |> addOnExited (fun data exitCode ->
            if exitCode <> 0 then
                let output = tryGetOutput (data :> obj)
                let msg =
                    match output with
                    | Some output ->
                        (sprintf "Process exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" exitCode r.CommandLine output.Output output.Error)
                    | None ->
                        (sprintf "Process exit code '%d' <> 0. Command Line: %s" exitCode r.CommandLine)                
                failwith msg
            else
                data
                )
    
    let warnOnExitCode msg (r:CreateProcess<_>) =
        r
        |> addOnExited (fun data exitCode ->
            if exitCode <> 0 then
                let output = tryGetOutput (data :> obj)
                let msg =
                    match output with
                    | Some output ->
                        (sprintf "%s. exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" msg exitCode r.CommandLine output.Output output.Error)
                    | None ->
                        (sprintf "%s. exit code '%d' <> 0. Command Line: %s" msg exitCode r.CommandLine)
                //if Env.isVerbose then
                eprintfn "%s" msg    
            else data)

    /// Ensures the executable is run with the full framework. On non-windows platforms that means running the tool by invoking 'mono'.
    let withFramework (c:CreateProcess<_>) =
        match Environment.isWindows, c.Command, Process.monoPath with
        | false, RawCommand(file, args), Some monoPath when file.ToLowerInvariant().EndsWith(".exe") ->
            { c with
                Command = RawCommand(monoPath, Arguments.withPrefix ["--debug"; file] args) }
        | false, RawCommand(file, args), _ when file.ToLowerInvariant().EndsWith(".exe") ->
            failwithf "trying to start a .NET process on a non-windows platform, but mono could not be found. Try to set the MONO environment variable or add mono to the PATH."
        | _ -> c
        

    type internal TimeoutState =
        { Stopwatch : System.Diagnostics.Stopwatch
          mutable Process : Process }    
    let withTimeout (timeout:System.TimeSpan) (c:CreateProcess<_>) =
        let mutable startTime = None 
        c
        |> appendSimpleFuncs 
            (fun _ -> 
                { Stopwatch = System.Diagnostics.Stopwatch.StartNew()
                  Process = null })
            (fun state proc -> 
                state.Process <- proc
                state.Stopwatch.Restart()
                async {
                    do! Async.Sleep(int timeout.TotalMilliseconds)
                    if not proc.HasExited then
                        try
                            proc.Kill()
                        with exn ->
                            Trace.traceError 
                            <| sprintf "Could not kill process %s  %s after timeout: %O" proc.StartInfo.FileName 
                                   proc.StartInfo.Arguments exn
                }
                |> Async.StartImmediate)
            (fun prev state exitCode -> 
                async {
                    let! e = exitCode |> Async.AwaitTask
                    state.Stopwatch.Stop()
                    let! prevResult = prev
                    match e.RawExitCode with
                    | 0 ->
                        return prevResult
                    | _ when state.Stopwatch.Elapsed > timeout -> 
                        failwithf "Process '%s' timed out." c.CommandLine
                    | _ ->
                        return prevResult
                })
            (fun state -> state.Stopwatch.Stop())


type AsyncProcessResult<'a> = { Result : System.Threading.Tasks.Task<'a>; Raw : System.Threading.Tasks.Task<RawProcessResult> }

module Proc =
    let startRaw (c:CreateProcess<_>) =
      async {
        let hook = c.Hook
        
        use state = hook.PrepareState ()
        let procRaw =
          { Command = c.Command
            WorkingDirectory = c.WorkingDirectory
            Environment = c.Environment
            Streams = c.Streams
            OutputHook =
                { new IRawProcessHook with
                    member x.Prepare streams = hook.PrepareStreams(state, streams)
                    member x.OnStart (p) = hook.ProcessStarted (state, p) } }

        let! exitCode = RawProc.processStarter.Start(procRaw)
        
        let output = 
            hook.RetrieveResult (state, exitCode)
            |> Async.StartImmediateAsTask

        return { Result = output; Raw = exitCode }
(*
        let o, realResult =
            match output with
            | Some f -> f, true
            | None -> { Output = ""; Error = "" }, false

        let strip (s:string) =
            let subString (s:string) =
                let splitMax = 300
                let half = splitMax / 2
                if s.Length < splitMax then s
                else sprintf "%s [...] %s" (s.Substring(0, half)) (s.Substring(s.Length - half))
                
            if s.Length < 1000 then
                s
            else
                let splits = s.Split([|"\n"|], System.StringSplitOptions.None)
                if splits.Length <= 1 then
                    // We need to use substring
                    subString s
                else
                    splits
                    |> Seq.take 10
                    |> fun s -> Seq.append s [" [ ... ] "]
                    |> fun s -> Seq.append s (splits |> Seq.skip (splits.Length - 10))
                    |> Seq.map subString
                    |> fun s -> System.String.Join("\n", s)
                    
        let strippedOutput = lazy strip o.Output
        let strippedError = lazy strip o.Error
        if realResult then
            Trace.tracefn "Process Output: %s, Error: %s" strippedOutput.Value strippedError.Value

        let result =
            try c.GetResult o
            with e ->
                let msg =
                    if realResult then
                        sprintf "Could not parse output from process, StdOutput: %s, StdError %s" strippedOutput.Value strippedError.Value
                    else
                        "Could not parse output from process, but RawOutput was not retrieved."
                raise <| System.Exception(msg, e)
        
        do! hook.ParseSuccess exitCode
        return { ExitCode = exitCode; CreateProcess = c; Result = result }*)
      }
      // Immediate makes sure we set the ref cell before we return the task...
      |> Async.StartImmediateAsTask
    
    let start c = 
        async {
            let! result = startRaw c
            return! result.Result |> Async.AwaitTask
        }
        |> Async.StartImmediateAsTask

    /// Convenience method when you immediatly want to await the result of 'start', just note that
    /// when used incorrectly this might lead to race conditions 
    /// (ie if you use StartAsTask and access reference cells in CreateProcess after that returns)
    let startAndAwait c = start c |> Async.AwaitTaskWithoutAggregate

    let runRaw c = (startRaw c).Result
    let run c = startAndAwait c |> Async.RunSynchronously
