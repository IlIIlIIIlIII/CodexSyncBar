using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Ubuntu.Backend;

namespace ClaudeSyncBar.App.Model;

/// <summary>An error the account service reported, with a message meant for the user.</summary>
public sealed class ServiceError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Calls the account service in this process with the same commands and JSON
/// shapes the Ubuntu app sends over its socket, off the UI thread.
/// </summary>
public sealed class ServiceClient(IManagementService service)
{
    public IManagementService Service => service;

    public async Task<JsonNode?> CallAsync(string method, object? parameters = null, TimeSpan? timeout = null)
    {
        var element = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, Wire.Json);
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            var result = await Task.Run(() => service.DispatchAsync(method, element, cancel.Token), cancel.Token);
            return result is null ? null : JsonSerializer.SerializeToNode(result, Wire.Json);
        }
        catch (RpcException error) { throw new ServiceError(error.Code, error.Message); }
        catch (OperationCanceledException) { throw new ServiceError("timeout", "응답 시간이 초과됐어요. 잠시 후 다시 시도해 주세요."); }
        catch (ServiceError) { throw; }
        catch (Exception) { throw new ServiceError("operation_failed", "작업을 완료하지 못했어요. 연결 상태와 계정 설정을 확인해 주세요."); }
    }
}
