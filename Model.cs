class ChatCompletionMessage
{
    public string Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

class OllamaChatCompletionResponse
{
    public string? Error { get; set; }
    public ChatCompletionMessage? Message { get; set; }
}