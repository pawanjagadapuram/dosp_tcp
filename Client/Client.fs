module Client

open System.Net.Sockets
open System.IO

type Client() =
    let newClient = new TcpClient()

    (*
        This function listens to the stream from server and displays the
        relevant message or takes relevant actions based on the messages/error codes
        that the server sends.
    *)
    member private this.listenAsync() =
        async {
            let stream = newClient.GetStream()
            use streamReader = new StreamReader(stream)

            while true do
                let! currentLine = streamReader.ReadLineAsync() |> Async.AwaitTask
                (* 
                    We consider negative numbers as error codes in here and display the 
                    respective meaning of the error code on the terminal to make the program
                    user friendly.
                *)
                if (currentLine = "-5") then
                    printf "Message from server: %s\n" currentLine
                    printf "exit"
                    this.closeConnection ()
                elif (currentLine = "-4") then
                    printf "Message from server: %s (One or more of the inputs contain(s) non-number(s).)\n" currentLine
                elif (currentLine = "-3") then
                    printf "Message from server: %s (Number of inputs is more than four.)\n" currentLine
                elif (currentLine = "-2") then
                    printf "Message from server: %s (Number of inputs is less than two.)\n" currentLine
                elif (currentLine = "-1") then
                    printf "Message from server: %s (Incorrect operation command.)\n" currentLine
                else
                    (*
                        If the response is not an error code i.e. not a negative number we
                        simply output the response from the server.
                    *)
                    printf "\nMessage from server: %s\n" currentLine

                (* 
                    Once we have received a response from server and done the relevant action if
                    in case the response was an error code we start taking input from the user.
                    This is done in this specific order since we only have to take inputs from
                    user after we have received a "Hello!" in response from the server when the 
                    client connection is initialised.
                *)
                Async.Start(this.readUserInput())
        }

    member this.readUserInput() =
        (*
            This function simply reads a line input from the user's terminal
            and writes that input to the server using the write function.
        *)
        async {
            printf "Input your message: \n"
            let msgToSend = System.Console.ReadLine()
            this.write (msgToSend) |> ignore
        }

    member this.connectToServer(host, port) =
        async {
            do!
            (* 
                Connecting to the server asynchronously and waiting for the connection, we
                start this connection as a task running asynchronously.  
            *)
                (newClient.ConnectAsync(host = host, port = port))
                |> Async.AwaitIAsyncResult
                |> Async.Ignore
            
            (*
                We start listening to the server resposes once the connection is made
                and start this listening process as an async task itself.
            *)
            this.listenAsync () |> Async.StartAsTask |> ignore
        }
        |> Async.StartAsTask

    member this.closeConnection() =
        (*
            This function closes the client connection and exits the program.
        *)
        newClient.Close()
        System.Environment.Exit(0)


    member this.write msg =
        (*
            This function checks the client is connected and initiates a stream writer
            using which we write our inputs to our server.

            Once the stream is written we flush the stream.

            This process of writing the stream is started as a task of itself.
        *)
        match newClient.Connected with
        | true ->
            async {
                let writerForStream = new StreamWriter(newClient.GetStream())

                do!
                    writerForStream.WriteLineAsync(msg.ToString())
                    |> Async.AwaitIAsyncResult
                    |> Async.Ignore

                do! writerForStream.FlushAsync() |> Async.AwaitIAsyncResult |> Async.Ignore
            }
            |> Async.StartAsTask
        | _ -> failwith "Client not connected"

    (*
        We implement this interface in our type of make sure everything gets 
        disposed gracefully.
    *)
    interface System.IDisposable with
        member this.Dispose() = newClient.Close()


(* 
    Written to let us not stop the program from finishing after one 
    execution of the connect function.
*)
let rec runIndefinitely () =
    System.Threading.Thread.Sleep(1000)
    runIndefinitely ()

[<EntryPoint>]

let main argv =
    use newClient = new Client()
    (* 
        Creating a connection to server on localhost or it can even be an ip address.
    *)
    let connection = newClient.connectToServer ("localhost", 9000)

    runIndefinitely ()
    0
