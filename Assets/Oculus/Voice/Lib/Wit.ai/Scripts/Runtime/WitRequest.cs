﻿/*
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the license found in the
 * LICENSE file in the root directory of this source tree.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Facebook.WitAi.Configuration;
using Facebook.WitAi.Data;
using Facebook.WitAi.Data.Configuration;
using Facebook.WitAi.Lib;
using UnityEngine;
using SystemInfo = UnityEngine.SystemInfo;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Facebook.WitAi
{
    /// <summary>
    /// Manages a single request lifecycle when sending/receiving data from Wit.ai.
    ///
    /// Note: This is not intended to be instantiated directly. Requests should be created with the
    /// WitRequestFactory
    /// </summary>
    public class WitRequest
    {
        /// <summary>
        /// Error code thrown when an exception is caught during processing or
        /// some other general error happens that is not an error from the server
        /// </summary>
        public const int ERROR_CODE_GENERAL = -1;

        /// <summary>
        /// Error code returned when no configuration is defined
        /// </summary>
        public const int ERROR_CODE_NO_CONFIGURATION = -2;

        /// <summary>
        /// Error code returned when the client token has not been set in the
        /// Wit configuration.
        /// </summary>
        public const int ERROR_CODE_NO_CLIENT_TOKEN = -3;

        /// <summary>
        /// No data was returned from the server.
        /// </summary>
        public const int ERROR_CODE_NO_DATA_FROM_SERVER = -4;

        /// <summary>
        /// Invalid data was returned from the server.
        /// </summary>
        public const int ERROR_CODE_INVALID_DATA_FROM_SERVER = -5;

        /// <summary>
        /// Request was aborted
        /// </summary>
        public const int ERROR_CODE_ABORTED = -6;

        /// <summary>
        /// Request to the server timeed out
        /// </summary>
        public const int ERROR_CODE_TIMEOUT = -7;

        public const string URI_SCHEME = "https";
        public const string URI_AUTHORITY = "api.wit.ai";

        public const string WIT_API_VERSION = "20210928";
        public const string WIT_SDK_VERSION = "0.0.20";

        public const string WIT_ENDPOINT_SPEECH = "speech";
        public const string WIT_ENDPOINT_MESSAGE = "message";
        public const string WIT_ENDPOINT_ENTITIES = "entities";
        public const string WIT_ENDPOINT_INTENTS = "intents";
        public const string WIT_ENDPOINT_TRAITS = "traits";
        public const string WIT_ENDPOINT_APPS = "apps";
        public const string WIT_ENDPOINT_UTTERANCES = "utterances";

        private WitConfiguration configuration;

        private Stream activeStream;

        private string command;
        private string path;

        public QueryParam[] queryParams;

        private HttpWebRequest request;
        private HttpWebResponse response;

        private WitResponseNode responseData;

        private bool isActive;
        private bool responseStarted;

        public byte[] postData;
        public string postContentType;

        private object streamLock = new object();

        /// <summary>
        /// Callback called when a response is received from the server
        /// </summary>
        public Action<WitRequest> onResponse;

        /// <summary>
        /// Callback called when the server is ready to receive data from the WitRequest's input
        /// stream. See WitRequest.Write()
        /// </summary>
        public Action<WitRequest> onInputStreamReady;

        /// <summary>
        /// Returns the raw string response that was received before converting it to a JSON object.
        ///
        /// NOTE: This response comes back on a different thread. Do not attempt ot set UI control
        /// values or other interactions from this callback. This is intended to be used for demo
        /// and test UI, not for regular use.
        /// </summary>
        public Action<string> onRawResponse;

        /// <summary>
        /// Returns a partial utterance from an in process request
        ///
        /// NOTE: This response comes back on a different thread.
        /// </summary>
        public Action<string> onPartialTranscription;

        /// <summary>
        /// Returns a full utterance from a completed request
        ///
        /// NOTE: This response comes back on a different thread.
        /// </summary>
        public Action<string> onFullTranscription;

        public delegate Uri OnCustomizeUriEvent(UriBuilder uriBuilder);
        /// <summary>
        /// Provides an opportunity to customize the url just before a request executed
        /// </summary>
        public OnCustomizeUriEvent onCustomizeUri;

        public delegate Dictionary<string, string> OnProvideCustomHeadersEvent();
        /// <summary>
        /// Provides an opportunity to provide custom headers for the request just before it is
        /// executed.
        /// </summary>
        public OnProvideCustomHeadersEvent onProvideCustomHeaders;

        /// <summary>
        /// Returns true if a request is pending. Will return false after data has been populated
        /// from the response.
        /// </summary>
        public bool IsActive => isActive;

        /// <summary>
        /// JSON data that was received as a response from the server after onResponse has been
        /// called
        /// </summary>
        public WitResponseNode ResponseData => responseData;

        /// <summary>
        /// Encoding settings for audio based requests
        /// </summary>
        public AudioEncoding audioEncoding = new AudioEncoding();

        private int statusCode;
        public int StatusCode => statusCode;

        private string statusDescription;
        private bool isRequestStreamActive;
        public bool IsRequestStreamActive => IsActive && isRequestStreamActive;

        public bool HasResponseStarted => responseStarted;

        private bool isServerAuthRequired;
        public string StatusDescription => statusDescription;

        public int Timeout => configuration ? configuration.timeoutMS : 10000;

        private static string operatingSystem;
        private static string deviceModel;
        private static string deviceName;
        private bool configurationRequired;
        private string serverToken;
        private string callingStackTrace;
        private DateTime requestStartTime;
        private ConcurrentQueue<byte[]> writeBuffer = new ConcurrentQueue<byte[]>();

        public override string ToString()
        {
            return path;
        }

        public WitRequest(WitConfiguration configuration, string path,
            params QueryParam[] queryParams)
        {
            if (!configuration) throw new ArgumentException("Configuration is not set.");
            configurationRequired = true;
            this.configuration = configuration;
            this.command = path.Split('/').First();
            this.path = path;
            this.queryParams = queryParams;

            if (null == operatingSystem) operatingSystem = SystemInfo.operatingSystem;
            if (null == deviceModel) deviceModel = SystemInfo.deviceModel;
            if (null == deviceName) deviceName = SystemInfo.deviceName;
        }

        public WitRequest(WitConfiguration configuration, string path, bool isServerAuthRequired,
            params QueryParam[] queryParams)
        {
            if (!isServerAuthRequired && !configuration)
                throw new ArgumentException("Configuration is not set.");
            configurationRequired = true;
            this.configuration = configuration;
            this.isServerAuthRequired = isServerAuthRequired;
            this.command = path.Split('/').First();
            this.path = path;
            this.queryParams = queryParams;
            if (isServerAuthRequired)
            {
                serverToken = WitAuthUtility.GetAppServerToken(configuration?.application?.id);
            }
        }

        public WitRequest(string serverToken, string path, params QueryParam[] queryParams)
        {
            configurationRequired = false;
            this.isServerAuthRequired = true;
            this.command = path.Split('/').First();
            this.path = path;
            this.queryParams = queryParams;
            this.serverToken = serverToken;
        }

        /// <summary>
        /// Key value pair that is sent as a query param in the Wit.ai uri
        /// </summary>
        public class QueryParam
        {
            public string key;
            public string value;
        }

        /// <summary>
        /// Start the async request for data from the Wit.ai servers
        /// </summary>
        public void Request()
        {
            responseStarted = false;

            UriBuilder uriBuilder = new UriBuilder();

            var endpointConfig = WitEndpointConfig.GetEndpointConfig(configuration);

            uriBuilder.Scheme = endpointConfig.UriScheme;

            uriBuilder.Host = endpointConfig.Authority;

            var api = endpointConfig.WitApiVersion;
            if (endpointConfig.port > 0)
            {
                uriBuilder.Port = endpointConfig.port;
            }

            uriBuilder.Query = $"v={api}";

            uriBuilder.Path = path;

            callingStackTrace = Environment.StackTrace;

            if (queryParams.Any())
            {
                var p = queryParams.Select(par =>
                    $"{par.key}={Uri.EscapeDataString(par.value)}");
                uriBuilder.Query += "&" + string.Join("&", p);
            }

            var uri = null == onCustomizeUri ? uriBuilder.Uri : onCustomizeUri(uriBuilder);
            StartRequest(uri);
        }

        private void StartRequest(Uri uri)
        {
            if (!configuration && configurationRequired)
            {
                statusDescription = "Configuration is not set. Cannot start request.";
                Debug.LogError(statusDescription);
                statusCode = ERROR_CODE_NO_CONFIGURATION;
                SafeInvoke(onResponse);
                return;
            }

            if (!isServerAuthRequired && string.IsNullOrEmpty(configuration.clientAccessToken))
            {
                statusDescription = "Client access token is not defined. Cannot start request.";
                Debug.LogError(statusDescription);
                statusCode = ERROR_CODE_NO_CLIENT_TOKEN;
                SafeInvoke(onResponse);
                return;
            }

            request = (HttpWebRequest) WebRequest.Create(uri);

            if (isServerAuthRequired)
            {
                request.Headers["Authorization"] =
                    $"Bearer {serverToken}";
            }
            else
            {
                request.Headers["Authorization"] =
                    $"Bearer {configuration.clientAccessToken.Trim()}";
            }

            if (null != postContentType)
            {
                request.Method = "POST";
                request.ContentType = postContentType;
                request.ContentLength = postData.Length;
            }

            // Configure additional headers
            if (WitEndpointConfig.GetEndpointConfig(configuration).Speech == command)
            {
                request.ContentType = audioEncoding.ToString();
                request.Method = "POST";
                request.SendChunked = true;
            }

            var configId = "not-yet-configured";
#if UNITY_EDITOR
            if (configuration)
            {
                if (string.IsNullOrEmpty(configuration.configId))
                {
                    configuration.configId = Guid.NewGuid().ToString();
                    EditorUtility.SetDirty(configuration);
                }

                configId = configuration.configId;
            }
#endif

            request.UserAgent = $"voice-sdk-35.0.0.98.175,wit-unity-{WIT_SDK_VERSION},{operatingSystem},{deviceModel},{configId},{Application.identifier}";

#if UNITY_EDITOR
            request.UserAgent += ",Editor";
#else
            request.UserAgent += ",Runtime";
#endif

            requestStartTime = DateTime.Now;
            isActive = true;
            statusCode = 0;
            statusDescription = "Starting request";
            request.Timeout = configuration ? configuration.timeoutMS : 10000;

            if (null != onProvideCustomHeaders)
            {
                foreach (var header in onProvideCustomHeaders())
                {
                    request.Headers[header.Key] = header.Value;
                }
            }
            if (request.Method == "POST")
            {
                var getRequestTask = request.BeginGetRequestStream(HandleRequestStream, request);
                ThreadPool.RegisterWaitForSingleObject(getRequestTask.AsyncWaitHandle,
                    HandleTimeoutTimer, request, Timeout, true);
            }
            else
            {
                StartResponse();
            }
        }

        private void StartResponse()
        {
            var result = request.BeginGetResponse(HandleResponse, request);
            ThreadPool.RegisterWaitForSingleObject(result.AsyncWaitHandle, HandleTimeoutTimer,
                request, Timeout, true);
        }

        private void HandleTimeoutTimer(object state, bool timedout)
        {
            if (!timedout) return;

            // Clean up the current request if it is still going
            var request = (HttpWebRequest) state;
            if (null != this.request)
            {
                Debug.Log("Request timed out after " + (DateTime.Now - requestStartTime));
                request.Abort();
            }

            isActive = false;

            // Close any open stream resources and clean up streaming state flags
            CloseRequestStream();

            // Update the error state to indicate the request timed out
            statusCode = ERROR_CODE_TIMEOUT;
            statusDescription = "Request timed out.";

            SafeInvoke(onResponse);
        }

        private void HandleResponse(IAsyncResult ar)
        {
            string stringResponse = "";
            responseStarted = true;
            try
            {
                response = (HttpWebResponse) request.EndGetResponse(ar);

                statusCode = (int) response.StatusCode;
                statusDescription = response.StatusDescription;

                try
                {
                    var responseStream = response.GetResponseStream();
                    if (response.Headers["Transfer-Encoding"] == "chunked")
                    {
                        byte[] buffer = new byte[10240];
                        int bytes = 0;
                        int offset = 0;
                        int totalRead = 0;
                        while ((bytes = responseStream.Read(buffer, offset, buffer.Length - offset)) > 0)
                        {
                            totalRead += bytes;
                            stringResponse = Encoding.UTF8.GetString(buffer, 0, totalRead);
                            if (stringResponse.Length > 0)
                            {
                                try
                                {
                                    responseData = WitResponseJson.Parse(stringResponse);

                                    offset = 0;

                                    totalRead = 0;
                                    if (null != responseData)
                                    {
                                        var transcription = responseData["text"];
                                        if (!string.IsNullOrEmpty(transcription))
                                        {
                                            onPartialTranscription?.Invoke(transcription);
                                        }
                                    }
                                }
                                catch (JSONParseException e)
                                {
                                    // TODO: t105419819 Update the protocol to better handle this issue.
                                    // This is a bit of a hack to get around an issue with a full
                                    // socket buffer or partial server response. We will need to
                                    // address this server side to make sure we're reading all data
                                    // rather than relying on a json parse exception to catch this.
                                    // Test case: Utterance with multiple entity responses pushing
                                    // final data > 1024 bytes.
                                    offset = bytes;
                                    Debug.LogWarning("Received what appears to be a partial response or invalid json. Attempting to continue reading. Parsing error: " + e.Message);
                                }
                            }
                        }

                        if (stringResponse.Length > 0 && null != responseData)
                        {
                            onFullTranscription?.Invoke(responseData["text"]);
                            onRawResponse?.Invoke(stringResponse);
                        }
                    }
                    else
                    {
                        using (StreamReader reader = new StreamReader(responseStream))
                        {
                            stringResponse = reader.ReadToEnd();
                            onRawResponse?.Invoke(stringResponse);
                            responseData = WitResponseJson.Parse(stringResponse);
                        }
                    }

                    responseStream.Close();
                }
                catch (JSONParseException e)
                {
                    Debug.LogError("Server returned invalid data: " + e.Message + "\n" +
                                   stringResponse);
                    statusCode = ERROR_CODE_INVALID_DATA_FROM_SERVER;
                    statusDescription = "Server returned invalid data.";
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"{e.Message}\nRequest Stack Trace:\n{callingStackTrace}\nResponse Stack Trace:\n{e.StackTrace}");
                    statusCode = ERROR_CODE_GENERAL;
                    statusDescription = e.Message;
                }

                response.Close();
            }
            catch (WebException e)
            {
                statusCode = (int) e.Status;
                if (e.Response is HttpWebResponse errorResponse)
                {
                    statusCode = (int) errorResponse.StatusCode;

                    try
                    {
                        var stream = errorResponse.GetResponseStream();
                        if (null != stream)
                        {
                            using (StreamReader reader = new StreamReader(stream))
                            {
                                stringResponse = reader.ReadToEnd();
                                onRawResponse?.Invoke(stringResponse);
                                responseData = WitResponseJson.Parse(stringResponse);
                            }
                        }
                    }
                    catch (JSONParseException)
                    {
                        // Response wasn't encoded error, ignore it.
                    }
                    catch (Exception errorResponseError)
                    {
                        // We've already caught that there is an error, we'll ignore any errors
                        // reading error response data and use the status/original error for validation
                        Debug.LogWarning(errorResponseError);
                    }
                }

                statusDescription = e.Message;
                if (e.Status != WebExceptionStatus.RequestCanceled)
                {
                    Debug.LogError(
                        $"Http Request Failed [{statusCode}]: {e.Message}\nRequest Stack Trace:\n{callingStackTrace}\nResponse Stack Trace:\n{e.StackTrace}");
                }
            }
            finally
            {
                isActive = false;
            }

            CloseRequestStream();

            if (null != responseData)
            {
                var error = responseData["error"];
                if (!string.IsNullOrEmpty(error))
                {
                    statusDescription = $"Error: {responseData["code"]}. {error}";
                    statusCode = statusCode == 200 ? ERROR_CODE_GENERAL : statusCode;
                }
            }
            else if (statusCode == 200)
            {
                statusCode = ERROR_CODE_NO_DATA_FROM_SERVER;
                statusDescription = "Server did not return a valid json response.";
                Debug.LogWarning(
                    "No valid data was received from the server even though the request was successful. Actual potential response data: \n" +
                    stringResponse);
            }

            SafeInvoke(onResponse);
        }

        private void HandleRequestStream(IAsyncResult ar)
        {
            StartResponse();
            var stream = request.EndGetRequestStream(ar);

            if (null != postData)
            {
                stream.Write(postData, 0, postData.Length);
                CloseRequestStream();
            }
            else
            {
                if (null == onInputStreamReady)
                {
                    CloseRequestStream();
                }
                else
                {
                    isRequestStreamActive = true;
                    SafeInvoke(onInputStreamReady);
                }
            }

            new Thread(ExecuteWriteThread).Start(stream);
        }

        private void ExecuteWriteThread(object obj)
        {
            Stream stream = (Stream) obj;


            try
            {
                while (isRequestStreamActive)
                {
                    FlushBuffer(stream);
                    Thread.Yield();
                }

                FlushBuffer(stream);
                stream.Close();
            }
            catch (ObjectDisposedException)
            {
                // Handling edge case where stream is closed remotely
                // This problem occurs when the Web server resets or closes the connection after
                // the client application sends the HTTP header.
                // https://support.microsoft.com/en-us/topic/fix-you-receive-a-system-objectdisposedexception-exception-when-you-try-to-access-a-stream-object-that-is-returned-by-the-endgetrequeststream-method-in-the-net-framework-2-0-bccefe57-0a61-517a-5d5f-2dce0cc63265
                Debug.LogWarning(
                    "Stream already disposed. It is likely the server reset the connection before streaming started.");
            }
            catch (IOException e)
            {
                Debug.LogWarning(e.Message);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private void FlushBuffer(Stream stream)
        {
            while (writeBuffer.Count > 0)
            {
                if (writeBuffer.TryDequeue(out var buffer))
                {
                    stream.Write(buffer, 0, buffer.Length);
                }
            }
        }

        private void SafeInvoke(Action<WitRequest> action)
        {
            // We want to allow each invocation to run even if there is an exception thrown by one
            // of the callbacks in the invocation list. This protects shared invocations from
            // clients blocking things like UI updates from other parts of the sdk being invoked.
            foreach (var responseDelegate in action.GetInvocationList())
            {
                try
                {
                    responseDelegate.DynamicInvoke(this);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                }
            }
        }

        public void AbortRequest()
        {
            CloseRequestStream();
            Debug.Log("Abort");
            request.Abort();
            statusCode = ERROR_CODE_ABORTED;
            statusDescription = "Request was aborted";
            isActive = false;
        }

        /// <summary>
        /// Method to close the input stream of data being sent during the lifecycle of this request
        ///
        /// If a post method was used, this will need to be called before the request will complete.
        /// </summary>
        public void CloseRequestStream()
        {
            lock (streamLock)
            {
                isRequestStreamActive = false;
            }
        }

        /// <summary>
        /// Write request data to the Wit.ai post's body input stream
        ///
        /// Note: If the stream is not open (IsActive) this will throw an IOException.
        /// Data will be written synchronously. This should not be called from the main thread.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        public void Write(byte[] data, int offset, int length)
        {
            // TODO: This is going to cause additional allocations, we can probably improve this
            var buffer = new byte[data.Length];
            Array.Copy(data, offset, buffer, 0, length);
            writeBuffer.Enqueue(buffer);
        }
    }
}
