using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Application.Booking;
using Tas.Domain;
using Tas.Infrastructure;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>The notification log through the API: who can see it, filtering, re-sending a failed message.</summary>
public sealed class NotificationApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private async Task<long> AddFailedNotificationAsync(string appointmentNo)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
        var appointment = await db.Appointments.SingleAsync(a => a.AppointmentNo == appointmentNo);
        var row = new NotificationLog
        {
            AppointmentId = appointment.AppointmentId,
            UserId = appointment.CreatedBy,
            Channel = NotificationChannel.Email,
            TemplateCode = NotificationTemplates.StatusRed,
            Language = "en",
            Status = NotificationStatus.Failed,
            Attempts = 5,
            LastError = "SMTP error: mailbox full",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.NotificationLogs.Add(row);
        await db.SaveChangesAsync();
        return row.NotificationId;
    }

    [Fact]
    public async Task Terminal_staff_see_the_log_and_planners_can_resend_failed_messages()
    {
        if (!Enabled) return;

        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        var planner = await As(DemoDataSeeder.PlannerEmail);
        var booked = await Read<AppointmentDto>(await dispatcher.PostAsJsonAsync(Appointments, ImportBooking()));
        var id = await AddFailedNotificationAsync(booked.AppointmentNo);
        var log = $"{Terminal}/notifications";

        // Companies have no access to the terminal's notification log.
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.GetAsync(log)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsync($"{log}/{id}/retry", null)).StatusCode);

        var all = await Read<List<NotificationView>>(await planner.GetAsync(log));
        var row = Assert.Single(all, n => n.NotificationId == id);
        Assert.Equal(booked.AppointmentNo, row.AppointmentNo);
        Assert.Equal(NotificationStatus.Failed, row.Status);
        Assert.Equal("User", row.RecipientType);
        Assert.Equal("SMTP error: mailbox full", row.LastError);

        Assert.Contains(await Read<List<NotificationView>>(await planner.GetAsync($"{log}?status=Failed&appointmentNo={booked.AppointmentNo}")), n => n.NotificationId == id);
        Assert.DoesNotContain(await Read<List<NotificationView>>(await planner.GetAsync($"{log}?status=Sent")), n => n.NotificationId == id);

        Assert.Equal(HttpStatusCode.Accepted, (await planner.PostAsync($"{log}/{id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsync($"{log}/{id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.PostAsync($"{log}/999999/retry", null)).StatusCode);

        var requeued = Assert.Single(await Read<List<NotificationView>>(await planner.GetAsync($"{log}?appointmentNo={booked.AppointmentNo}")), n => n.NotificationId == id);
        Assert.Equal(NotificationStatus.Queued, requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.Null(requeued.LastError);
    }
}
