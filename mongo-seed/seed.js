// Idempotent seed — safe to run multiple times.
// Drops and re-inserts products so stock levels are always predictable.

print("Starting Commerce Hub seed...");

// Clear existing products to ensure clean state
db.Products.deleteMany({});

db.Products.insertMany([
  {
    _id: ObjectId("000000000000000000000001"),
    name: "Widget Pro",
    sku: "WGT-PRO-001",
    price: NumberDecimal("29.99"),
    stockQuantity: 100
  },
  {
    _id: ObjectId("000000000000000000000002"),
    name: "Gadget Basic",
    sku: "GDG-BSC-002",
    price: NumberDecimal("14.50"),
    stockQuantity: 50
  },
  {
    _id: ObjectId("000000000000000000000003"),
    name: "Thingamajig Elite",
    sku: "TMJ-ELT-003",
    price: NumberDecimal("89.00"),
    stockQuantity: 5   // Intentionally low to test insufficient-stock path
  }
]);

// Indexes for performance and data integrity
db.Products.createIndex({ sku: 1 }, { unique: true });
db.Products.createIndex({ stockQuantity: 1 });  // Supports the Gte stock-guard filter

db.Orders.createIndex({ customerId: 1 });
db.Orders.createIndex({ status: 1 });
db.Orders.createIndex({ createdAt: -1 });  // Descending for recent-first queries

print("Seed complete:");
print("  - 3 products inserted");
print("  - Indexes created on Products (sku unique, stockQuantity) and Orders (customerId, status, createdAt)");
print("");
print("Product IDs for testing:");
print("  000000000000000000000001 -> Widget Pro (stock: 100, price: $29.99)");
print("  000000000000000000000002 -> Gadget Basic (stock: 50, price: $14.50)");
print("  000000000000000000000003 -> Thingamajig Elite (stock: 5, price: $89.00)");
