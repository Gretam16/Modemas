using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Modemas.Server
{
    /// <summary>
    /// A SignalR Hub that is responsible for managing lobbies.
    /// <para>It's responsible for:</para>
    /// <list type="bullet">
    ///   <item><description>Creating lobbies</description></item>
    ///   <item><description>Joining lobbies</description></item>
    ///   <item><description>Tracking lobby membership</description></item>
    ///   <item><description>Cleaning up when players disconnect</description></item>
    ///   <item><description>Broadcasting updates to all players in a lobby</description></item>
    /// </list>
    /// </summary>
    public class LobbyHub : Hub
    {
        private static readonly ConcurrentDictionary<string, Lobby> Lobbies = new();

        /// <summary>
        /// Creates a new lobby. Adds the connection calling this method as a host.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task CreateLobby(string hostName)
        {
            Console.WriteLine($"Invoking CreateLobby.");
            var lobbyId = Guid.NewGuid().ToString("N").Substring(0, 8); // short code
            var lobby = new Lobby
            {
                LobbyId = lobbyId,
                HostConnectionId = Context.ConnectionId,
            };
            Lobbies[lobbyId] = lobby;

            await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);
            await Clients.Caller.SendAsync("LobbyCreated", lobbyId);
            Console.WriteLine($"Lobby {lobbyId} has been created.");

            try
            {
                await JoinLobby(lobbyId, hostName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error joining lobby immediately after creation: {ex}");
            }
        }

        /// <summary>
        /// Adds a player to a lobby, if not already present.
        /// </summary>
        /// <param name="lobbyId">The ID of the lobby to join.</param>
        /// <param name="playerName">The display name of the player. Cannot have multiple repeating names in the same lobby.</param>
        /// <returns>A task representing the async operation.</returns>
        public async Task JoinLobby(string lobbyId, string playerName)
        {
            Console.WriteLine($"Invoking JoinLobby {lobbyId}.");
            if (!Lobbies.TryGetValue(lobbyId, out var lobby))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found");
                return;
            }

            // This is probably not going to be needed now.
            // if (lobby.HostConnectionId == Context.ConnectionId)
            // {
            //     await Clients.Caller.SendAsync("Error", "Hosts can't join their own lobby.");
            //     return;
            // }

            if (lobby.Players.Any(p => p.ConnectionId == Context.ConnectionId))
            {
                await Clients.Caller.SendAsync("Error", "You are already part of the lobby");
                return;
            }

            if (lobby.Players.Any(p => p.Name == playerName))
            {
                await Clients.Caller.SendAsync("Error", $"A player with the name {playerName} already exists");
                return;
            }

            var player = new Player
            {
                Name = playerName,
                ConnectionId = Context.ConnectionId
            };

            lobby.Players.Add(player);

            await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);
            await Clients.Group(lobbyId).SendAsync("LobbyAddPlayer", playerName);
            await Clients.Caller.SendAsync("LobbyJoined", lobbyId, playerName, lobby.Players.Select(p => p.Name), lobby.State);

            Console.WriteLine($"Player {playerName} has joined lobby {lobbyId}.");
        }


        public async Task StartVoting(string lobbyId)
        {
            const int duration = 10; // Could probably be made a setting?

            await Clients.Group(lobbyId).SendAsync("VotingStarted", lobbyId, duration);
            Console.WriteLine($"Voting period started for {duration} seconds in lobby {lobbyId}.");

            await Task.Delay(duration * 1000);

            Console.WriteLine($"Voting period has ended in lobby {lobbyId}.");
            await Clients.Group(lobbyId).SendAsync("VotingEnded", lobbyId);
            await StartMatch(lobbyId);
        }

        /// <summary>
        /// Starts a the match in a lobby. Currently doesn't have much use other then setting lobbyState to LobbyState.Started.
        /// </summary>
        /// <param name="lobbyId">The ID of the lobby to join.</param>
        /// <returns>A task representing the async operation.</returns>
        public async Task StartMatch(string lobbyId)
        {
            if (!Lobbies.TryGetValue(lobbyId, out var lobby))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found");
                return;
            }

            if (lobby.State == LobbyState.Started)
            {
                await Clients.Caller.SendAsync("Error", "Cannot start a match while another is in progress.");
                return;
            }

            if (lobby.HostConnectionId != Context.ConnectionId)
            {
                await Clients.Caller.SendAsync("Error", "Only the host can start the match");
                return;
            }

            var json = System.IO.File.ReadAllText("questions.json");
            var questions = System.Text.Json.JsonSerializer.Deserialize<List<Question>>(json);

            if (questions == null)
            {
                await Clients.Caller.SendAsync("Error", "There are no questions available.");
                return;
            }

            lobby.Match.Questions = questions;
            lobby.Match.CurrentQuestionIndex = 0;
            lobby.State = LobbyState.Started;

            Console.WriteLine($"Match has been started in lobby {lobbyId}.");
            await Clients.Group(lobbyId).SendAsync("LobbyMatchStarted", lobbyId);
            await RunMatch(lobby); // There is something wrong with this method taht should probably be fixed later.
        }

        /// <summary>
        /// Runs the match loop for a specific lobby. 
        /// <para>This method sends each question to all clients in the lobby, waits for the question's time limit, 
        /// and then sends a timeout notification. Once all questions have been sent, the match ends and the lobby state is set back to <see cref="LobbyState.Waiting"/>.</para>
        /// </summary>
        /// <param name="lobby">The <see cref="Lobby"/> instance for which the match should be run.</param>
        /// <returns>A task representing the async operation.</returns>
        /// <remarks> This is a fire-and-forget method. </remarks>
        private async Task RunMatch(Lobby lobby)
        {
            lobby.State = LobbyState.Started;

            while (lobby.Match.CurrentQuestionIndex < lobby.Match.Questions.Count)
            {
                var question = lobby.Match.Questions[lobby.Match.CurrentQuestionIndex];
                await Clients.Group(lobby.LobbyId).SendAsync("NewQuestion", question);
                await Clients.Group(lobby.LobbyId).SendAsync("NewQuestion", new
                {
                    question.Text,
                    question.Choices,
                    lobby.LobbySettings.QuestionTimerInSeconds,
                });
                Console.WriteLine($"New Question: {question.Text}, {question.Choices}, {question.TimeLimit}");

                await Task.Delay(question.TimeLimit * 1000);

                await Clients.Group(lobby.LobbyId).SendAsync("QuestionTimeout", $"Timeout for question {lobby.Match.CurrentQuestionIndex}!");

                lobby.Match.CurrentQuestionIndex++;
            }

            lobby.State = LobbyState.Waiting;
            await Clients.Group(lobby.LobbyId).SendAsync("MatchEndStarted", lobby.LobbyId);

            Console.WriteLine($"Match in lobby {lobby.LobbyId} has been ended.");
            await StartMatchEnd(lobby.LobbyId);
        }

        /// <summary>
        /// Handles an answer submitted by a player for the current question in a specific lobby.
        /// <para>This method validates that the lobby exists, that the match is active, that the player is part of the lobby,
        /// and that the answer index is valid for the current question.</para>
        /// </summary>
        /// <param name="lobbyId">The ID of the lobby in which the answer is being submitted.</param>
        /// <param name="answerIndex">The index of the choice selected by the player.</param>
        /// <returns>A task representing the async operation.</returns>
        /// <remarks> Currently, this method does nothing. </remarks>
        public async Task AnswerQuestion(string lobbyId, int answerIndex)
        {
            if (!Lobbies.TryGetValue(lobbyId, out var lobby))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found.");
                return;
            }

            if (lobby.State != LobbyState.Started)
            {
                await Clients.Caller.SendAsync("Error", "No active match is running.");
                return;
            }

            var player = lobby.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("Error", "You are not part of this lobby.");
                return;
            }

            var currentQuestion = lobby.Match.Questions[lobby.Match.CurrentQuestionIndex];
            if (answerIndex < 0 || answerIndex >= currentQuestion.Choices.Count)
            {
                await Clients.Caller.SendAsync("Error", "Invalid answer choice.");
                return;
            }

            // This function doesn't actually do anything currently :)
            // ...
            Console.WriteLine($"Player '{player.Name}' answered question {lobby.Match.CurrentQuestionIndex} with option {answerIndex}.");
        }

        public async Task StartMatchEnd(string lobbyId)
        {
            const int duration = 10; // Could probably be made a setting?

            await Clients.Group(lobbyId).SendAsync("MatchEndStarted", lobbyId, duration);
            Console.WriteLine($"Match end period started for {duration} seconds in lobby {lobbyId}.");

            await Task.Delay(duration * 1000);

            await Clients.Group(lobbyId).SendAsync("MatchEndEnded", lobbyId);
            Console.WriteLine($"Match end period has ended in lobby {lobbyId}.");
        }

        /// <summary>
        /// Called automatically when a client disconnects from the hub.
        /// <para>This method checks whether the disconnecting client is a player or host in any lobby. 
        /// If a player disconnects, they are removed from the lobby and the remaining players are notified. 
        /// If the host disconnects, all players are kicked, the lobby is closed, and the lobby is removed from the global list.</para>
        /// </summary>
        /// <param name="exception">The exception that triggered the disconnect, if any.</param>
        /// <returns>A task representing the async operation.</returns>
        /// <remarks> This method ensures proper cleanup of lobbies and notification of clients when a disconnect occurs. </remarks>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            foreach (var kvp in Lobbies)
            {
                var lobby = kvp.Value;
                var player = lobby.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);

                if (player != null)
                {
                    lobby.Players.Remove(player);
                    await Clients.Group(lobby.LobbyId).SendAsync("PlayerLeft", player.Name);
                    break;
                }

                if (lobby.HostConnectionId == Context.ConnectionId)
                {
                    // Inform all players they are kicked
                    foreach (var p in lobby.Players.ToList())
                    {
                        await Clients.Client(p.ConnectionId).SendAsync("KickedFromLobby", "Host disconnected");
                        await Groups.RemoveFromGroupAsync(p.ConnectionId, lobby.LobbyId);
                    }
                    // Inform host too (if needed)
                    await Clients.Client(lobby.HostConnectionId).SendAsync("KickedFromLobby", "Host disconnected");
                    await Groups.RemoveFromGroupAsync(lobby.HostConnectionId, lobby.LobbyId);

                    await Clients.Group(lobby.LobbyId).SendAsync("Lobby closed", "Host disconnected, lobby closed");
                    Lobbies.TryRemove(lobby.LobbyId, out _);
                    break;
                }
            }

            await base.OnDisconnectedAsync(exception);
            Console.WriteLine("A player has been disconnected.");
        }

        /// <summary>
        /// Allows the host to update lobby customization settings.
        /// </summary>
        public async Task UpdateLobbySettings(string lobbyId, int numberOfQuestions, string theme, int questionTimerInSeconds)
        {
            if (!Lobbies.TryGetValue(lobbyId, out var lobby))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found.");
                return;
            }
            if (lobby.HostConnectionId != Context.ConnectionId)
            {
                await Clients.Caller.SendAsync("Error", "Only the host can update settings.");
                return;
            }
            lobby.LobbySettings.NumberOfQuestions = numberOfQuestions;
            lobby.LobbySettings.QuestionTimerInSeconds = questionTimerInSeconds;
            await Clients.Group(lobbyId).SendAsync("LobbySettingsUpdated", numberOfQuestions, theme, questionTimerInSeconds);

            Console.WriteLine($"Setting for lobby {lobbyId} have been updated.");
        }
    }
}
