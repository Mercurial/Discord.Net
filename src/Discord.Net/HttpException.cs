﻿using System;
using System.Net;

namespace Discord
{
	public class HttpException : Exception
	{
		public HttpStatusCode StatusCode { get; }

		public HttpException(HttpStatusCode statusCode)
			: base($"The server responded with error {statusCode}")
        {
			StatusCode = statusCode;
        }
	}
}
