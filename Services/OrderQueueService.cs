using PrintShop.Models;
using System.Collections.Concurrent;

namespace PrintShop.Services;

public class OrderQueueService
{
    private readonly ConcurrentDictionary<string, PrintOrder> _orders = new();

    public void Add(PrintOrder order) =>
        _orders[order.Id] = order;

    public PrintOrder? Get(string id) =>
        _orders.TryGetValue(id, out var order) ? order : null;

    public void Update(PrintOrder order) =>
        _orders[order.Id] = order;

    public IEnumerable<PrintOrder> GetAll() =>
        _orders.Values.OrderByDescending(o => o.CreatedAt);

    public IEnumerable<PrintOrder> GetPending() =>
        _orders.Values
            .Where(o => o.Status == OrderStatus.PaymentConfirmed || o.Status == OrderStatus.Printing)
            .OrderBy(o => o.CreatedAt);
}
