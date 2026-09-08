using Booking.Worker.Handling;
using Booking.Worker.Messaging;
using Booking.Worker.Storage;

// Booking worker.
//
// Consumes BookingCreated from Service Bus and writes confirmed bookings to its
// own store. It is a separate deployable from the API on purpose: a slow
// confirmation must never slow down accepting a booking, and the two scale on
// different signals (requests per second versus queue depth).

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ProcessedBookingStore>();
builder.Services.AddScoped<BookingCreatedHandler>();
builder.Services.AddHostedService<ServiceBusConsumer>();

var host = builder.Build();
await host.RunAsync();
