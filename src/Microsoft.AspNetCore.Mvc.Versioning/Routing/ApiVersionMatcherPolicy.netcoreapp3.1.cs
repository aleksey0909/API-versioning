﻿#if NETCOREAPP3_1
namespace Microsoft.AspNetCore.Mvc.Routing
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc.Versioning;
    using Microsoft.AspNetCore.Routing.Matching;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading.Tasks;
    using static Microsoft.AspNetCore.Mvc.Versioning.ErrorCodes;
    using static System.Threading.Tasks.Task;

    /// <content>
    /// Provides additional implementation specific to ASP.NET Core 3.0.
    /// </content>
    public partial class ApiVersionMatcherPolicy
    {
        /// <inheritdoc />
        public Task ApplyAsync( HttpContext httpContext, CandidateSet candidates )
        {
            if ( httpContext == null )
            {
                throw new ArgumentNullException( nameof( httpContext ) );
            }

            if ( candidates == null )
            {
                throw new ArgumentNullException( nameof( candidates ) );
            }

            if ( IsRequestedApiVersionAmbiguous( httpContext, out var apiVersion ) )
            {
                return CompletedTask;
            }

            if ( apiVersion == null && Options.AssumeDefaultVersionWhenUnspecified )
            {
                apiVersion = TrySelectApiVersion( httpContext, candidates );
                httpContext.Features.Get<IApiVersioningFeature>().RequestedApiVersion = apiVersion;
            }

            var finalMatches = EvaluateApiVersion( candidates, apiVersion );

            if ( finalMatches.Count == 0 )
            {
                httpContext.SetEndpoint( ClientError( httpContext, candidates ) );
            }
            else
            {
                for ( var i = 0; i < finalMatches.Count; i++ )
                {
                    var (index, _, valid) = finalMatches[i];
                    candidates.SetValidity( index, valid );
                }
            }

            return CompletedTask;
        }

        bool IsRequestedApiVersionAmbiguous( HttpContext httpContext, out ApiVersion? apiVersion )
        {
            try
            {
                apiVersion = httpContext.GetRequestedApiVersion();
            }
            catch ( AmbiguousApiVersionException ex )
            {
                Logger.LogInformation( ex.Message );
                apiVersion = default;

                var handlerContext = new RequestHandlerContext( Options.ErrorResponses )
                {
                    Code = AmbiguousApiVersion,
                    Message = ex.Message,
                };

                httpContext.SetEndpoint( new BadRequestHandler( handlerContext ) );
                return true;
            }

            return false;
        }
    }
}
#endif