import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";
import MainMenuView from "./views/MainMenuView.jsx";
import WaitingView from "./views/WaitingView.jsx";
import TopicChooserView from "./views/TopicChooserView.jsx";
import MatchView from "./views/MatchView.jsx";
import MatchEndView from "./views/MatchEndView.jsx";
import "./App.css";


const LobbyState = {
    Waiting: 0,
    Started: 1,
    Closed: 2,
}

function App() {
    // Program elements
    const [connection, setConnection] = useState(null);
    const [lobbyId, setLobbyId] = useState(null);
    const [playerName, setPlayerName] = useState(null);
    const [players, setPlayers] = useState([]);
    const [lobbyState, setLobbyState] = useState(null);
    const [isHost, setIsHost] = useState(false);
    const [question, setQuestion] = useState(null);

    // Helper to connect to SignalR hub
    const connectToHub = async () => {
        const newConnection = new signalR.HubConnectionBuilder()
            .withUrl("/lobbyhub")
            .withAutomaticReconnect()
            .build();

        // ***********************************************
        // Put events received from the backend here:
        // ***********************************************
        newConnection.on("LobbyCreated", (lobbyId, lobbyState) => {
            setLobbyId(lobbyId);
            setLobbyState(lobbyState);
            setIsHost(true);
            console.log(lobbyState)
        });
        newConnection.on("LobbyJoined", (lobbyId, playerName, players, lobbyState) => {
            setLobbyId(lobbyId);
            setPlayerName(playerName);
            setPlayers(players);
            setLobbyState(lobbyState);
            setIsHost(false);
            console.log(lobbyState)
        });
        newConnection.on("LobbyAddPlayer", (playerName) => {
            setPlayers((prev) => {
                if (prev.includes(playerName)) return prev;
                return [...prev, playerName];
            });
        });
        newConnection.on("LobbyMatchStarted", (lobbyState) => {
            setLobbyState(lobbyState);
        });
        newConnection.on("Error", (errorMsg) => {
            console.log(errorMsg);
        });
        newConnection.on("KickedFromLobby", async (message) => {
            await newConnection.stop();
            alert("You were kicked out of the room: " + message);
            setLobbyId(null);
            setPlayerName(null);
            setPlayers([]);
            setLobbyState(null);
            setIsHost(false);
            await connectToHub();
        });
        newConnection.on("NewQuestion", (question) => {
            setQuestion(question);
            setTimeLeft(question.timeLimit);
        });
        newConnection.on("QuestionTimeout", (QuestionTimeoutMessage) => {
            console.log(`${QuestionTimeoutMessage}`);
        });
        newConnection.on("MatchEnded", (lobbyId) => {
            if (lobbyId == lobbyId) {
                setLobbyState(LobbyState.Waiting);
                setQuestion(null);
                console.log("Match ended! Returning to lobby");
            }
        });
        try {
            await newConnection.start();
            setConnection(newConnection);
        } catch (err) {
            console.error("Connection failed: ", err);
        }
    };

    useEffect(() => {
        connectToHub();
    }, []);

    // Picks which view to render
    let view;
    if (!lobbyId) {
        view = (
            <MainMenuView
                connection={connection}
            />
        );
    } else if (lobbyState === LobbyState.Waiting) {
        view = (
            <WaitingView
                connection={connection}
                lobbyId={lobbyId}
                lobbyState={lobbyState}
                playerName={playerName}
                players={players}
                isHost={isHost}
            />
        );
    } else if (lobbyState === LobbyState.Started && question != null) {
        // view = <div>Game Started! (Placeholder for future GameView)</div>;
        view = (
            <MatchView
                connection={connection}
                lobbyId={lobbyId}
                question={question}
            />
        );
    } else {
        view = (
            <WaitingView
                connection={connection}
                lobbyId={lobbyId}
                lobbyState={lobbyState}
                playerName={playerName}
                players={players}
                isHost={isHost}
                onStartMatch={() => startMatch(lobbyId)}
            />
        );
    }
    return (
        <div className="App">
            {view}
        </div>
    );
}

export default App;
