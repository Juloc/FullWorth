using System.Text.Json;
using FullWorth.Backend.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Validation;

public sealed class ProblemExceptionHandlerTests
{
    [Fact]
    public async Task Writes500ProblemWithoutLeakingTheException()
    {
        var problemDetails = new RecordingProblemDetailsService();
        var handler = new ProblemExceptionHandler(problemDetails, NullLogger<ProblemExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        var secret = "Host=db;Password=hunter2-should-never-surface";

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException(secret), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var problem = problemDetails.Last;
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem!.ProblemDetails.Status);
        Assert.Equal("An unexpected error occurred.", problem.ProblemDetails.Title);
        Assert.Null(problem.ProblemDetails.Detail);
        // Nothing derived from the exception (message, stack) may appear in the serialized body.
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(problem.ProblemDetails));
    }

    private sealed class RecordingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? Last { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Last = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Last = context;
            return ValueTask.FromResult(true);
        }
    }
}
