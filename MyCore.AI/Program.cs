using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024;
});

builder.Services.AddKernel();
builder.Services.AddGoogleAIGeminiChatCompletion(
    modelId: builder.Configuration["GoogleAIGemini:Model"] ?? throw new InvalidOperationException("Missing GoogleAIGemini:Model from orchestrator."),
    apiKey: builder.Configuration["GoogleAIGemini:Key"] ?? throw new InvalidOperationException("Missing GoogleAIGemini:Key from orchestrator.")
);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<MyCore.AI.Services.ConversationMemory>();
builder.Services.AddSingleton<MyCore.AI.Services.FastTranscriber>();
builder.Services.AddSingleton<MyCore.AI.Services.LlmOrchestrator>();
builder.Services.AddSingleton<MyCore.AI.Services.TtsStreamer>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapHub<MyCore.AI.Hubs.ChatHub>("/chat");

app.Run();
