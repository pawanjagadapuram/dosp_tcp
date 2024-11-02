module Server

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

// Create a global mutable value for the TCP Listener. We will later bind this to an instance of TcpListener when we create the server
let mutable listener: TcpListener = null

// Create cancellation token source to handle cancellation of server
let server_cts = new CancellationTokenSource()

// Create a mutable list of clients with additional data to manage the client pool
let mutable clientTaskList: (string * CancellationTokenSource * Task * TcpClient) list =
    []

// Helper function to add a client to the client list
let addClientTask client =
    clientTaskList <- client :: clientTaskList

// Helper function to asynchronously write to a client using the NetworkStream / StreamWriter
let sendToClient client message =
    let streamWriter = new StreamWriter(stream = client)

    async {
        // We await the execution of WriteLineAsync with Async.AwaitIAsyncResult
        do!
            streamWriter.WriteLineAsync(message.ToString())
            |> Async.AwaitIAsyncResult
            |> Async.Ignore

        // Asynchronously flush the stream to ensure all buffers are written to the client
        do! streamWriter.FlushAsync() |> Async.AwaitIAsyncResult |> Async.Ignore
    }

// Function to loop through each client in the list and send a message to each one. Each sendToClient computation is queued to be executed in parallel with the Async.Parallel construct
let broadcastToConnectedClients message =

    clientTaskList
    |> List.map (fun (_, _, _, client) -> sendToClient (client.GetStream()) message)
    |> Async.Parallel
    |> Async.Ignore

// Function to disconnect a client gracefully and remove it from the client list
let disconnectClient (clientID: string) =
    let c = clientTaskList |> List.find (fun (id, _, _, _) -> id.Equals(clientID))
    let (id, cts, task, client) = c
    let endpoint = (client.Client.RemoteEndPoint :?> IPEndPoint).Address.ToString()
    cts.Cancel() //Cancel the task which handles this client's incoming requests
    client.GetStream().Dispose() //Release all resources of the NetworkStream used for this client
    client.Dispose() //Release resources used by the TcpClient
    client.Close() //Close the socket connection with the client
    printfn "Disconnected Client %s (client ID = %s)" endpoint clientID

    clientTaskList <- clientTaskList |> List.filter (fun (cID, _, _, _) -> cID <> id) //Remove client from the client list

// This function iterates through the client list and calls disconnectClient on each client
let disconnectAllClients () =
    clientTaskList |> List.iter (fun (id, _, _, _) -> disconnectClient id)

// Function to shutdown server gracefully
let shutdownServer () =
    listener.Stop() //Close the listener
    server_cts.Cancel() //Cancel the task handling the server logic
    printfn "%s" "Shutting down server"
    Environment.Exit(0) //Exit the program

// Helper function to perform mathematical operations on the operands
let operate (op: string, operands: string array) =
    let mutable result = 0

    if op.Equals("multiply") then
        result <- 1

    if op.Equals("subtract") then
        result <- 2 * (operands[0] |> int)

    for item in operands do
        match op with
        | "add" -> result <- result + (item |> int)
        | "subtract" -> result <- result - (item |> int)
        | "multiply" -> result <- result * (item |> int)
        | _ -> result <- result

    result.ToString()

// Async Function (unit) that handles the client requests and response for each client
let listenToClient (clientId: string, client: TcpClient, token: CancellationToken) =
    async {
        use stream = client.GetStream()
        let endpoint = (client.Client.RemoteEndPoint :?> IPEndPoint).Address.ToString()
        let! _ = sendToClient stream "Hello!"
        use reader = new StreamReader(stream = stream)

        try
            try
                while not token.IsCancellationRequested && client.Connected do
                    let! line = reader.ReadLineAsync() |> Async.AwaitTask

                    // Line = null implies that the client is disconnected (0 byte End of Message (EOM) is sent upon disconnection). In that case, we exit and do not execute further
                    if (line = null) then
                        return ()
                    else
                        printfn "Received command from client %s (ID:%s): %s" endpoint clientId line

                        let parts = line.Trim().Split " " //Split the operation command and operands
                        let mutable res = ""

                        let operation = parts[0].ToLower()
                        let operands = parts[1 .. (parts.Length - 1)]
                        let mutable isValidInput = true

                        // Check for non-number operands
                        for o in operands do
                            try
                                let _ = (o |> int)
                                ()
                            with e ->
                                isValidInput <- false

                        // Error / Exception Handling
                        if not (List.contains operation [ "add"; "subtract"; "multiply"; "bye"; "terminate" ]) then
                            res <- "-1"
                        elif operation.Contains("bye") then
                            do! sendToClient stream "-5"
                            disconnectClient (clientId)
                        elif operation.Contains("terminate") then
                            do! broadcastToConnectedClients "-5"
                            disconnectAllClients ()
                            shutdownServer ()
                        elif operands.Length < 2 then
                            res <- "-2"
                        elif operands.Length > 4 then
                            res <- "-3"
                        elif not isValidInput then
                            res <- "-4"
                        else
                            // If no errors/exceptions, run operate and get the result
                            res <- operate (operation, operands)

                        if not (operation.Contains("bye")) && not (operation.Contains("terminate")) then
                            do! sendToClient stream res
                            printfn "Responded to client %s with result: %s" endpoint res
            with
            | :? OperationCanceledException -> printfn "%s" "Operation Cancelled"
            | :? SocketException -> printfn "%s" "Socket error"

        finally
            client.Dispose()
            client.Close()
            clientTaskList <- clientTaskList |> List.filter (fun (cID, _, _, _) -> cID <> clientId)
    }

// Function to start the TCP Listener, accept incoming client connections and create async tasks for each client connection
let createServer port =
    let serverWorkflow =
        async {
            listener <- new TcpListener(IPAddress.Any, port) //Bind listener to a new TCPListener instance
            listener.Start() //Start listening for incoming connections
            printfn "TCP Server started and listening on Port %i" port

            try
                try
                    while not server_cts.Token.IsCancellationRequested do
                        let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
                        let endpoint = (client.Client.RemoteEndPoint :?> IPEndPoint).Address.ToString()
                        let id = Guid.NewGuid().ToString() //Create a GUID for each client
                        printfn "Client with ID %s connected from: %s" id endpoint

                        //Create a CancellationTokenSource for each client to cancel the async tasks handling each client
                        let cts = new CancellationTokenSource()

                        // With Async.StartAsTask, we spawn an asynchronous task to handle each client's requests (listenToClient function) which executes in the thread pool
                        let clientListenTask = Async.StartAsTask(listenToClient (id, client, cts.Token))

                        // Add client to the client list
                        addClientTask (id, cts, clientListenTask, client)
                with
                // Handle Socket exceptions and Cancellations
                | :? SocketException as ex when ex.SocketErrorCode = SocketError.Interrupted ->
                    printfn "%s" "Server was stopped"
                | :? OperationCanceledException as ex -> printfn "%s" "Listener cancelled"
            finally
                listener.Stop()

        }

    Async.StartAsTask(serverWorkflow, cancellationToken = server_cts.Token) //Run the server workflow as an Async task

[<EntryPoint>]
let main argv =

    let _ = createServer (9000) //Application entry point which creates the server

    Console.ReadLine() |> ignore
    0 // return an integer exit code
