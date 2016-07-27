﻿namespace Microsoft.Web.Http.Versioning
{
    using FluentAssertions;
    using System;
    using System.Net.Http;
    using Xunit;

    public class DefaultApiVersionSelectorTest
    {
        [Fact]
        public void select_version_should_return_default_api_version()
        {
            // arrange
            var options = new ApiVersioningOptions();
            var selector = new DefaultApiVersionSelector( options );
            var request = new HttpRequestMessage();
            var model = ApiVersionModel.Default;
            var version = new ApiVersion( 1, 0 );

            // act
            var selectedVersion = selector.SelectVersion( request, model );

            // assert
            selectedVersion.Should().Be( version );
        }

        [Fact]
        public void select_version_should_return_updated_default_api_version()
        {
            // arrange
            var options = new ApiVersioningOptions();
            var selector = new DefaultApiVersionSelector( options );
            var request = new HttpRequestMessage();
            var model = ApiVersionModel.Default;
            var version = new ApiVersion( 42, 0 );

            options.DefaultApiVersion = version;

            // act
            var selectedVersion = selector.SelectVersion( request, model );

            // assert
            selectedVersion.Should().Be( version );
        }
    }
}
