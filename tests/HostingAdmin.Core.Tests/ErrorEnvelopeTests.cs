using HostingAdmin.Core.ErrorHandling;
using Xunit;

namespace HostingAdmin.Core.Tests;

public class ErrorEnvelopeTests
{
    [Fact]
    public void InternalError_HasCanonicalCodeAndGenericMessage()
    {
        var envelope = ErrorEnvelopeFactory.InternalError("trace-1");

        Assert.Equal(ErrorCodes.InternalError, envelope.Error.Code);
        Assert.Equal(ErrorEnvelopeFactory.GenericInternalErrorMessage, envelope.Error.Message);
        Assert.Equal("trace-1", envelope.Error.TraceId);
        Assert.Null(envelope.Error.Details);
    }

    [Fact]
    public void InternalError_MessageDoesNotContainExceptionDetails()
    {
        var envelope = ErrorEnvelopeFactory.InternalError("trace-1");

        const string forbidden = "at HostingAdmin";
        Assert.DoesNotContain(forbidden, envelope.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", envelope.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", envelope.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationFailed_CarriesFieldDetails()
    {
        var envelope = ErrorEnvelopeFactory.ValidationFailed(
            [new ErrorDetail("take", "Must be between 1 and 200.")], "trace-2");

        Assert.Equal(ErrorCodes.ValidationFailed, envelope.Error.Code);
        var detail = Assert.Single(envelope.Error.Details!);
        Assert.Equal("take", detail.Field);
        Assert.Equal("Must be between 1 and 200.", detail.Message);
    }

    [Theory]
    [InlineData(ErrorCodes.Unauthorized)]
    [InlineData(ErrorCodes.Forbidden)]
    [InlineData(ErrorCodes.RateLimited)]
    [InlineData(ErrorCodes.CapabilityUnavailable)]
    public void Factory_ProducesExpectedCodes(string expectedCode)
    {
        var envelope = expectedCode switch
        {
            ErrorCodes.Unauthorized => ErrorEnvelopeFactory.Unauthorized("t"),
            ErrorCodes.Forbidden => ErrorEnvelopeFactory.Forbidden("t"),
            ErrorCodes.RateLimited => ErrorEnvelopeFactory.RateLimited("t"),
            _ => ErrorEnvelopeFactory.CapabilityUnavailable("t"),
        };

        Assert.Equal(expectedCode, envelope.Error.Code);
        Assert.Equal("t", envelope.Error.TraceId);
    }
}
