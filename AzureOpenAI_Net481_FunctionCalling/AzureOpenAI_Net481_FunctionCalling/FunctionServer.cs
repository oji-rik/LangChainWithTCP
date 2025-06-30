using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using AzureOpenAI_Net481_FunctionCalling.Models;

namespace AzureOpenAI_Net481_FunctionCalling
{
    public class FunctionServer
    {
        private HttpListener _listener;
        private readonly string _baseUrl = "http://localhost:8080/";
        private bool _isRunning = false;

        public async Task StartAsync()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(_baseUrl);
                _listener.Start();
                _isRunning = true;

                Console.WriteLine($"Function Server started at {_baseUrl}");
                Console.WriteLine("Available endpoints:");
                Console.WriteLine("  GET /tools - Get available tool definitions");
                Console.WriteLine("  POST /execute - Execute a function");
                Console.WriteLine("Press 'q' to quit server...");

                var serverTask = Task.Run(async () =>
                {
                    while (_isRunning && _listener.IsListening)
                    {
                        try
                        {
                            var context = await _listener.GetContextAsync();
                            _ = Task.Run(() => ProcessRequestAsync(context));
                        }
                        catch (ObjectDisposedException)
                        {
                            // Expected when stopping the listener
                            break;
                        }
                        catch (HttpListenerException ex)
                        {
                            Console.WriteLine($"HTTP Listener error: {ex.Message}");
                            break;
                        }
                    }
                });

                // Wait for user input to quit
                while (_isRunning)
                {
                    var key = Console.ReadKey(true);
                    if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        break;
                    }
                }

                await StopAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting server: {ex.Message}");
                throw;
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
                Console.WriteLine("Function Server stopped.");
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                // Handle OPTIONS preflight request
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string responseString = "";
                response.ContentType = "application/json";

                try
                {
                    if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/tools")
                    {
                        responseString = GetToolDefinitions();
                        response.StatusCode = 200;
                    }
                    else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/execute")
                    {
                        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                        {
                            var requestBody = await reader.ReadToEndAsync();
                            responseString = await ExecuteFunctionAsync(requestBody);
                            response.StatusCode = 200;
                        }
                    }
                    else
                    {
                        responseString = JsonConvert.SerializeObject(new { error = "Not Found" });
                        response.StatusCode = 404;
                    }
                }
                catch (Exception ex)
                {
                    responseString = JsonConvert.SerializeObject(new { error = ex.Message });
                    response.StatusCode = 500;
                    Console.WriteLine($"Request processing error: {ex.Message}");
                }

                var buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();

                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {request.HttpMethod} {request.Url.AbsolutePath} - {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private string GetToolDefinitions()
        {
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Name = "prime_factorization",
                    Description = "整数を素因数分解する",
                    Parameters = new ParametersSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, PropertySchema>
                        {
                            {
                                "number", new PropertySchema
                                {
                                    Type = "integer",
                                    Description = "対象の整数"
                                }
                            }
                        },
                        Required = new[] { "number" }
                    }
                },
                new ToolDefinition
                {
                    Name = "sum",
                    Description = "整数リストの合計を計算する",
                    Parameters = new ParametersSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, PropertySchema>
                        {
                            {
                                "list", new PropertySchema
                                {
                                    Type = "array",
                                    Description = "合計を計算する整数のリスト",
                                    Items = new PropertySchema { Type = "integer" }
                                }
                            }
                        },
                        Required = new[] { "list" }
                    }
                }
            };

            var toolsResponse = new ToolsResponse { Tools = tools };
            return JsonConvert.SerializeObject(toolsResponse, Formatting.Indented);
        }

        private async Task<string> ExecuteFunctionAsync(string requestBody)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<FunctionRequest>(requestBody);
                
                if (request == null)
                {
                    return JsonConvert.SerializeObject(
                        FunctionResponse.CreateError("", "Invalid request format"));
                }

                Console.WriteLine($"Function call: {request.FunctionName} with arguments: {string.Join(", ", request.Arguments.Keys)}");

                object result = null;

                switch (request.FunctionName.ToLower())
                {
                    case "prime_factorization":
                        var numberObj = GetArgumentValue(request.Arguments, "number", "n", "num", "value", "integer");
                        if (numberObj != null)
                        {
                            var number = Convert.ToInt32(numberObj);
                            Console.WriteLine($"  → prime_factorization({number})");
                            result = PrimeFactorization(number);
                        }
                        else
                        {
                            var availableArgs = string.Join(", ", request.Arguments.Keys);
                            return JsonConvert.SerializeObject(
                                FunctionResponse.CreateError(request.RequestId, 
                                    $"Missing number argument. Expected: 'number', 'n', 'num', 'value', or 'integer'. Received: {availableArgs}"));
                        }
                        break;

                    case "sum":
                        var listObj = GetArgumentValue(request.Arguments, "list", "numbers", "values", "arr", "data", "items");
                        if (listObj != null)
                        {
                            List<int> numbers;
                            
                            if (listObj is Newtonsoft.Json.Linq.JArray jArray)
                            {
                                numbers = jArray.ToObject<List<int>>();
                            }
                            else if (listObj is List<object> objList)
                            {
                                numbers = objList.Select(Convert.ToInt32).ToList();
                            }
                            else if (listObj is object[] objArray)
                            {
                                numbers = objArray.Select(Convert.ToInt32).ToList();
                            }
                            else
                            {
                                return JsonConvert.SerializeObject(
                                    FunctionResponse.CreateError(request.RequestId, $"Invalid list format: {listObj.GetType().Name}"));
                            }
                            
                            Console.WriteLine($"  → sum([{string.Join(", ", numbers)}])");
                            result = Sum(numbers);
                        }
                        else
                        {
                            var availableArgs = string.Join(", ", request.Arguments.Keys);
                            return JsonConvert.SerializeObject(
                                FunctionResponse.CreateError(request.RequestId, 
                                    $"Missing list argument. Expected: 'list', 'numbers', 'values', 'arr', 'data', or 'items'. Received: {availableArgs}"));
                        }
                        break;

                    default:
                        return JsonConvert.SerializeObject(
                            FunctionResponse.CreateError(request.RequestId, $"Unknown function: {request.FunctionName}"));
                }

                Console.WriteLine($"  → Result: {result}");
                return JsonConvert.SerializeObject(
                    FunctionResponse.CreateSuccess(request.RequestId, result));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Function execution error: {ex.Message}");
                return JsonConvert.SerializeObject(
                    FunctionResponse.CreateError("", $"Function execution error: {ex.Message}"));
            }
        }

        /// <summary>
        /// Helper method to get argument value by trying multiple possible parameter names.
        /// This handles LangChain's tendency to use different parameter names than what's defined in the schema.
        /// </summary>
        private static object GetArgumentValue(Dictionary<string, object> arguments, params string[] possibleNames)
        {
            foreach (string name in possibleNames)
            {
                if (arguments.ContainsKey(name))
                {
                    return arguments[name];
                }
            }
            return null;
        }

        // Function implementations
        private static List<int> PrimeFactorization(int n)
        {
            var list = new List<int>();
            for (int i = 2; i * i <= n; i++)
            {
                while (n % i == 0)
                {
                    list.Add(i);
                    n /= i;
                }
            }
            if (n > 1) list.Add(n);
            return list;
        }

        private static int Sum(List<int> numbers)
        {
            return numbers.Sum();
        }
    }
}