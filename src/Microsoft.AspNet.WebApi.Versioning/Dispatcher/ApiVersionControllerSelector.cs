﻿namespace Microsoft.Web.Http.Dispatcher
{
    using Microsoft.Web.Http.Controllers;
    using Microsoft.Web.Http.Routing;
    using Microsoft.Web.Http.Versioning;
    using Microsoft.Web.Http.Versioning.Conventions;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Net.Http;
    using System.Web.Http;
    using System.Web.Http.Controllers;
    using System.Web.Http.Dispatcher;
    using static Controllers.HttpControllerDescriptorComparer;
    using static System.StringComparer;
    using static Versioning.ErrorCodes;

    /// <summary>
    /// Represents the logic for selecting a versioned controller.
    /// </summary>
    public class ApiVersionControllerSelector : IHttpControllerSelector
    {
        readonly HttpConfiguration configuration;
        readonly ApiVersioningOptions options;
        readonly HttpControllerTypeCache controllerTypeCache;
        readonly Lazy<ConcurrentDictionary<string, HttpControllerDescriptorGroup>> controllerInfoCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApiVersionControllerSelector"/> class.
        /// </summary>
        /// <param name="configuration">The <see cref="HttpConfiguration">configuration</see> to initialize
        /// the controller selector with.</param>
        /// <param name="options">The <see cref="ApiVersioningOptions">service versioning options</see>
        /// associated with the controller selector.</param>
        public ApiVersionControllerSelector( HttpConfiguration configuration, ApiVersioningOptions options )
        {
            Arg.NotNull( configuration, nameof( configuration ) );
            Arg.NotNull( options, nameof( options ) );

            this.configuration = configuration;
            this.options = options;
            controllerInfoCache = new Lazy<ConcurrentDictionary<string, HttpControllerDescriptorGroup>>( InitializeControllerInfoCache );
            controllerTypeCache = new HttpControllerTypeCache( this.configuration );
        }

        /// <summary>
        /// Creates and returns a controller descriptor mapping.
        /// </summary>
        /// <returns>A <see cref="IDictionary{TKey,TValue}">collection</see> of route-to-controller mapping.</returns>
        public virtual IDictionary<string, HttpControllerDescriptor> GetControllerMapping()
        {
            Contract.Ensures( Contract.Result<IDictionary<string, HttpControllerDescriptor>>() != null );

            var mapping = controllerInfoCache.Value.Where( p => p.Value.Count > 0 );
            return mapping.ToDictionary( p => p.Key, p => (HttpControllerDescriptor) p.Value, OrdinalIgnoreCase );
        }

        /// <summary>
        /// Selects and returns the controller descriptor to invoke given the provided request.
        /// </summary>
        /// <param name="request">The <see cref="HttpRequestMessage">request</see> to get a controller descriptor for.</param>
        /// <returns>The <see cref="HttpControllerDescriptor">controller descriptor</see> that matches the specified <paramref name="request"/>.</returns>
        public virtual HttpControllerDescriptor SelectController( HttpRequestMessage request )
        {
            Arg.NotNull( request, nameof( request ) );
            Contract.Ensures( Contract.Result<HttpControllerDescriptor>() != null );

            EnsureRequestHasValidApiVersion( request );

            var context = new ControllerSelectionContext( request, GetControllerName, controllerInfoCache );
            var conventionRouteSelector = new ConventionRouteControllerSelector( options, controllerTypeCache );
            var conventionRouteResult = default( ControllerSelectionResult );
            var exceptionFactory = new HttpResponseExceptionFactory( request, new Lazy<ApiVersionModel>( () => context.AllVersions ) );

            if ( context.RouteData == null )
            {
                conventionRouteResult = conventionRouteSelector.SelectController( context );

                if ( conventionRouteResult.Succeeded )
                {
                    return conventionRouteResult.Controller;
                }

                throw exceptionFactory.NewNotFoundOrBadRequestException( conventionRouteResult, default );
            }

            var directRouteSelector = new DirectRouteControllerSelector( options );
            var directRouteResult = directRouteSelector.SelectController( context );

            if ( directRouteResult.Succeeded )
            {
                return directRouteResult.Controller;
            }

            conventionRouteResult = conventionRouteSelector.SelectController( context );

            if ( conventionRouteResult.Succeeded )
            {
                return conventionRouteResult.Controller;
            }

            throw exceptionFactory.NewNotFoundOrBadRequestException( conventionRouteResult, directRouteResult );
        }

        /// <summary>
        /// Gets the name of the controller for the specified request.
        /// </summary>
        /// <param name="request">The <see cref="HttpRequestMessage">request</see> to the controller name for.</param>
        /// <returns>The name of the controller for the specified <paramref name="request"/>.</returns>
        public virtual string GetControllerName( HttpRequestMessage request )
        {
            Arg.NotNull( request, nameof( request ) );

            var routeData = request.GetRouteData();

            if ( routeData == null )
            {
                return null;
            }

            if ( routeData.Values.TryGetValue( RouteDataTokenKeys.Controller, out string controller ) )
            {
                return controller;
            }

            var configuration = request.GetConfiguration();
            var routes = configuration.Routes;
            var context = request.GetRequestContext();
            var virtualPathRoot = routes.VirtualPathRoot;

            if ( context != null )
            {
                virtualPathRoot = context.VirtualPathRoot ?? string.Empty;
            }

            for ( var i = 0; i < routes.Count; i++ )
            {
                var otherRouteData = routes[i].GetRouteData( virtualPathRoot, request );

                if ( otherRouteData != null &&
                    !routeData.Equals( otherRouteData ) &&
                     otherRouteData.Values.TryGetValue( RouteDataTokenKeys.Controller, out controller ) )
                {
                    break;
                }
            }

            return controller;
        }

        ConcurrentDictionary<string, HttpControllerDescriptorGroup> InitializeControllerInfoCache()
        {
            var options = configuration.GetApiVersioningOptions();
            var implicitVersionModel = new ApiVersionModel( options.DefaultApiVersion );
            var conventions = options.Conventions;
            var actionSelector = configuration.Services.GetActionSelector();
            var mapping = new ConcurrentDictionary<string, HttpControllerDescriptorGroup>( OrdinalIgnoreCase );

            foreach ( var pair in controllerTypeCache.Cache )
            {
                var key = pair.Key;
                var descriptors = new List<HttpControllerDescriptor>();

                foreach ( var grouping in pair.Value )
                {
                    foreach ( var type in grouping )
                    {
                        var descriptor = new HttpControllerDescriptor( configuration, key, type );

                        if ( conventions.Count == 0 || !conventions.ApplyTo( descriptor ) )
                        {
                            ApplyAttributeOrImplicitConventions( descriptor, actionSelector, implicitVersionModel );
                        }

                        descriptors.Add( descriptor );
                    }
                }

                descriptors.Sort( ByVersion );

                var descriptorGroup =
                    options.ReportApiVersions ?
                    new HttpControllerDescriptorGroup( configuration, key, ApplyCollatedModel( descriptors, actionSelector, CollateModel( descriptors ) ) ) :
                    new HttpControllerDescriptorGroup( configuration, key, descriptors.ToArray() );

                mapping.TryAdd( key, descriptorGroup );
            }

            return mapping;
        }

        static bool IsDecoratedWithAttributes( HttpControllerDescriptor controller )
        {
            Contract.Requires( controller != null );

            return controller.GetCustomAttributes<IApiVersionProvider>().Count > 0 ||
                   controller.GetCustomAttributes<IApiVersionNeutral>().Count > 0;
        }

        static void ApplyImplicitConventions( HttpControllerDescriptor controller, IHttpActionSelector actionSelector, ApiVersionModel implicitVersionModel )
        {
            Contract.Requires( controller != null );
            Contract.Requires( actionSelector != null );
            Contract.Requires( implicitVersionModel != null );

            controller.SetProperty( implicitVersionModel );

            var actions = actionSelector.GetActionMapping( controller ).SelectMany( g => g );

            foreach ( var action in actions )
            {
                action.SetProperty( implicitVersionModel );
            }
        }

        static void ApplyAttributeOrImplicitConventions( HttpControllerDescriptor controller, IHttpActionSelector actionSelector, ApiVersionModel implicitVersionModel )
        {
            Contract.Requires( controller != null );
            Contract.Requires( actionSelector != null );
            Contract.Requires( implicitVersionModel != null );

            if ( IsDecoratedWithAttributes( controller ) )
            {
                var conventions = new ControllerApiVersionConventionBuilder( controller.ControllerType );
                conventions.ApplyTo( controller );
            }
            else
            {
                ApplyImplicitConventions( controller, actionSelector, implicitVersionModel );
            }
        }

        static ApiVersionModel CollateModel( IEnumerable<HttpControllerDescriptor> controllers ) => controllers.Select( c => c.GetApiVersionModel() ).Aggregate();

        static HttpControllerDescriptor[] ApplyCollatedModel( List<HttpControllerDescriptor> controllers, IHttpActionSelector actionSelector, ApiVersionModel collatedModel )
        {
            Contract.Requires( controllers != null );
            Contract.Requires( actionSelector != null );
            Contract.Requires( collatedModel != null );
            Contract.Ensures( Contract.Result<HttpControllerDescriptor[]>() != null );

            foreach ( var controller in controllers )
            {
                var model = controller.GetApiVersionModel();
                var actions = actionSelector.GetActionMapping( controller ).SelectMany( g => g );

                controller.SetProperty( model.Aggregate( collatedModel ) );

                foreach ( var action in actions )
                {
                    model = action.GetApiVersionModel();
                    action.SetProperty( model.Aggregate( collatedModel ) );
                }
            }

            return controllers.ToArray();
        }

        static void EnsureRequestHasValidApiVersion( HttpRequestMessage request )
        {
            Contract.Requires( request != null );

            try
            {
                var apiVersion = request.GetRequestedApiVersion();
            }
            catch ( AmbiguousApiVersionException ex )
            {
                var options = request.GetApiVersioningOptions();
                throw new HttpResponseException( options.ErrorResponses.BadRequest( request, AmbiguousApiVersion, ex.Message ) );
            }
        }
    }
}