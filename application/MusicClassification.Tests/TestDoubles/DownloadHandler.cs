using System.Net;

namespace Mugnum.MusicClassificationUtility.Tests.TestDoubles;

/// <summary>
/// Supplies deterministic HTTP responses without accessing the network.
/// </summary>
internal sealed class DownloadHandler : HttpMessageHandler
{
	/// <summary>
	/// Bytes returned by successful download requests.
	/// </summary>
	private readonly byte[] _payload;

	/// <summary>
	/// Whether requests wait for cancellation instead of returning a response.
	/// </summary>
	private readonly bool _waitForCancellation;

	/// <summary>
	/// Creates a deterministic download transport.
	/// </summary>
	/// <param name="payload"> Successful response bytes. </param>
	/// <param name="waitForCancellation"> Whether requests remain pending until cancelled. </param>
	public DownloadHandler(byte[] payload, bool waitForCancellation = false)
	{
		ArgumentNullException.ThrowIfNull(payload);
		_payload = payload;
		_waitForCancellation = waitForCancellation;
	}

	/// <summary>
	/// Number of HTTP requests issued by the downloader.
	/// </summary>
	public int Requests { get; private set; }

	/// <summary>
	/// Returns fixture bytes or observes cancellation of a pending request.
	/// </summary>
	/// <param name="request"> Download request. </param>
	/// <param name="cancellationToken"> Request cancellation token. </param>
	/// <returns> Successful fixture response. </returns>
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		Requests++;
		if (_waitForCancellation)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) };
	}
}

