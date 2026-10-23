# Entity Framework Core from Alice: a DbContext subclass, entities, SaveChanges, and LINQ queries that EF
# translates to SQL (the lambdas compile to C# expression trees). Run: alice run examples/data/efcore
using Microsoft.EntityFrameworkCore

class Blog
  property id : Int32 = 0
  property name : String = ""
  property rating : Int32 = 0
end

class BlogDb < DbContext
  property blogs : DbSet(Blog)                     # EF fills DbSet properties
  def OnConfiguring(options : DbContextOptionsBuilder)
    options.UseSqlite("Data Source=#{System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alice-blogs.db")}")
  end
end

using db = BlogDb.new
  db.Database.EnsureDeleted
  db.Database.EnsureCreated
  db.blogs.Add(Blog.new.tap { |b| b.name = "alice"; b.rating = 5 })
  db.blogs.Add(Blog.new.tap { |b| b.name = "bob"; b.rating = 2 })
  db.blogs.Add(Blog.new.tap { |b| b.name = "carol"; b.rating = 4 })
  db.SaveChanges
  good = db.blogs.Where(->(b : Blog) { b.rating > 3 }).OrderBy(->(b : Blog) { b.name }).Select(->(b : Blog) { b.name }).ToList()
  puts "rated above 3: #{good.join(", ")}"
  puts "names starting with b: #{db.blogs.Count(->(b : Blog) { b.name.StartsWith("b") })}"
  puts "sql: #{db.blogs.Where(->(b : Blog) { b.rating > 3 }).ToQueryString.split("\n").last.strip}"
end
