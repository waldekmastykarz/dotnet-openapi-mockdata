using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using NJsonSchema;
using NJsonSchema.Validation;

var arrayPrompt = """
You are a service that returns sample data in JSON format based on the specified data shape in OpenAPI format. You reply only in JSON without any additional text or explanation. For example, for the following OpenAPI schema:

{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "age": {
      "type": "number"
    }
  }
}

You would respond with:

{
  "name": "John Doe",
  "age": 30
}

Create sample JSON data based on the following OpenAPI schema:

```
{schema}
```

The following is an array of 5 items, that match the specified data shape, in a JSON object with 2 spaces of indentation and no properties with the value undefined:
""";
var objectPrompt = """
You are a service that returns sample data in JSON format based on the specified data shape in OpenAPI format. You reply only in JSON without any additional text or explanation. For example, for the following OpenAPI schema:

{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "age": {
      "type": "number"
    }
  }
}

You would respond with:

{
  "name": "John Doe",
  "age": 30
}

Create sample JSON data based on the following OpenAPI schema:

```
{schema}
```

The following is an object, that matches the specified data shape, in a JSON object with 2 spaces of indentation and no properties with the value undefined:
""";
var errorPrompt = """
The JSON object is invalid for the following reason:
```
{error}
```
The following is a revised JSON object:
""";

string GetObjectId(string path, string operation, string response, string content) =>
  $"{path}.{operation}.{response}.{content}";
void Log(string? message = null) => Console.WriteLine($"[{DateTime.Now:T}] {message}");

async Task<string?> GetMockData(string schema, string type)
{
    var maxRetries = 3;
    var retries = 0;
    var messages = new List<ChatCompletionMessage>
    {
        new ChatCompletionMessage { Content = (type == "array" ? arrayPrompt : objectPrompt).Replace("{schema}", schema), Role = "user" }
    };

    while (retries < maxRetries)
    {
        var res = await GetMockDataInternal([.. messages]);
        if (res is null)
        {
            Log("Failed to get mock data");
            return null;
        }

        if (res.Error is not null)
        {
            Log(res.Error);
            return null;
        }

        Debug.Assert(res.Message is not null);
        Log("Response:");
        Log(res.Message.Content);

        try
        {
            var validationErrors = await IsValidObject(res.Message.Content, schema);
            if (validationErrors.Any())
            {
                throw new Exception(string.Join(", ", validationErrors.Select(e => e.ToString())));
            }
            Log("Valid");

            return res.Message.Content;
        }
        catch (Exception e)
        {
            if (++retries > maxRetries)
            {
                Log("Failed to get valid mock data");
                return null;
            }
            Log($"Failed: {e.Message}. Attempt {retries}");
            messages.Add(res.Message);
            messages.Add(new()
            {
                Content = errorPrompt.Replace("{error}", e.Message),
                Role = "user"
            });
        }
    }
    return null;
}

async Task<OllamaChatCompletionResponse?> GetMockDataInternal(ChatCompletionMessage[] messages)
{
    Log("Getting mock data...");
    Log("Prompt:");
    Log(messages.Last().Content);

    using var httpClient = new HttpClient();
    var response = await httpClient.PostAsJsonAsync("http://localhost:11434/api/chat", new
    {
        messages,
        model = "phi3",
        stream = false
    });
    return await response.Content.ReadFromJsonAsync<OllamaChatCompletionResponse>();
}

async Task<IEnumerable<ValidationError>> IsValidObject(string json, string schema)
{
    Log("Validating JSON object...");
    Log(json);
    Log("Schema:");
    Log(schema);
    var jsonSchema = await JsonSchema.FromJsonAsync(schema);
    return jsonSchema.Validate(json);
}

async Task<Dictionary<string, string>> GenerateMockData(string fileName)
{
    // objectId => mockObject
    Dictionary<string, string> mockObjects = new();
    var openApiDoc = new OpenApiStreamReader().Read(
        File.OpenRead($"specs/{fileName}"), out _);

    foreach (var path in openApiDoc.Paths)
    {
        Log($"Processing {path.Key}...");

        foreach (var operation in path.Value.Operations)
        {
            Log($"Processing {operation.Key}...");

            foreach (var response in operation.Value.Responses)
            {
                Log($"Processing {response.Key}...");

                foreach (var content in response.Value.Content)
                {
                    Log($"Processing {content.Key}...");

                    if (content.Value.Schema is null ||
                        (content.Value.Schema.Type != "object" &&
                        content.Value.Schema.Type != "array"))
                    {
                        Log("Schema empty or non-object/-array. Skipping");
                        continue;
                    }

                    var schema = content.Value.Schema.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
                    var mockData = await GetMockData(schema, content.Value.Schema.Type);

                    if (mockData is null)
                    {
                        continue;
                    }

                    var objectId = GetObjectId(path.Key, operation.Key.ToString().ToUpper(), response.Key, content.Key);
                    mockObjects.Add(objectId, mockData);
                }
            }
        }
    }

    foreach (var mockObject in mockObjects)
    {
        Log(mockObject.Key);
        Log(mockObject.Value);
        Log();
    }

    return mockObjects;
}

(string file, int expected)[] testFiles = [
    ("api.contoso.com-20240506103050.json", 1),
    ("graph.microsoft.com-20231213170747.json", 1),
    ("graph.microsoft.com-20231213170929.json", 1),
    ("graph.microsoft.com-20231214170131.json", 2),
    ("jsonplaceholder.typicode.com-20240528131349.json", 4)
];
foreach (var (file, expected) in testFiles)
{
    Log($"Testing {file}...");
    Debug.Assert((await GenerateMockData(file)).Count == expected, file);
}
