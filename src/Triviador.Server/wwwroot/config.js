// Where the game server's SignalR hub lives. Same origin while the server hosts the page;
// when the client moves to Azure Static Web Apps this becomes the Container App's URL.
window.TRIVIADOR_CONFIG = {
  hubUrl: "/hubs/game",
};
