This a fork that adds various features and bugfixes to the original EFUtilities by Mikael Eliasson. Below is a modified version of the original README.md.

## The goal

Add support for SQLite EF provider (included here is System.Data.SQLite) to perform bulk operations (Insert, Update). 
Add support for Async Tasks for the bulk operations
Use .Net v4.5 minimum 

## Installing

Right now this only works for DbContext. If anyone want to make a failing test or provide a sample project for any of the other variants it will probably be easy to fix.

### EF 6.3+

Any package from 1.0.3 and up should work.

No Nuget package yet

## Utility methods 

These methods are small helpers that make certain things easier. Most of them work against context so they should be provider independent. It will be stated when this is not the case.

Now has support for SQLite EF bulk operations.

### Update single values on an entity

REQUIRES: using EntityFramework.Utilities;

```c#
db.AttachAndModify(item).Set(x => x.Property, "NewValue")
```

A simpler API for working with disconnected entities and only updating single values. This is useful if you want to update a value on an entity without roundtripping the database. A typical usecase could be to update the number of reads of a blogpost. With this API it would look like this

```c#
using (var db = new YourDbContext())
 {
      db.AttachAndModify(new BlogPost { ID = postId }).Set(x => x.Reads, 10);
      db.SaveChanges();
}
```
The Set method is chainable so you could fluently add more properties to update.

This would send this single command to the database:
```
exec sp_executesql N'UPDATE [dbo].[BlogPosts]
SET [Reads] = @0
WHERE ([ID] = @1)
',N'@0 int,@1 int',@0=10,@1=1
```

### IncludeEFU (A significantly faster include)

REQUIRES: using EntityFramework.Utilities;

The standard EF Include is really really slow to use. They reason is that it cross joins the child records against the parent which means you load a significant amount of duplicate data. This means more data to transfer, more data to parse, more memory etc etc. 

Include EFU on the other hand runs two parallel queries and stitch the data toghether in memory.

A very basic query:

```c#
var result = db.Contacts
.IncludeEFU(db, c => c.PhoneNumbers)
.ToList();
```

It's also possible to sort and filter the child collections

```c#
var result = db.Contacts
.IncludeEFU(db, x => x.PhoneNumbers
    .Where(n => n.Number == "10134")
    .OrderBy(p => p.ContactId)
    .ThenByDescending(p => p.Number))
.ToList();
```

VERY IMPORTANT: The plan was to add support for nested collections and projections but it seemed like the next version of EF would have this problem fixed in the core so that functionality was dropped (it was hard to get right). What works right now is that you can include one or more child collections but only on the first level. 

Also it's important to know that the queries are run AsNoTracking(). If you use this method you are after read performance so you shouldn't need the tracking. If you might need to update some of the entities I suggest you just attach them back to the context.

### db.Database.ForceDelete()

REQUIRES: using EntityFramework.Utilities;

PROVIDER DEPENDENT: This methods uses raw sql to drop connections so only works against sql server

Drops the mssql database even if it has connections open. Solves the problem when you are recreating the database in your tests and Management Studio has an open connection that earlier prevented dropping the database. Super useful for testscenarios where you recreate the DB for each test but still need to debug with management studio.

## Batch operations

These methods all work outside the normal EF pipeline and are located on the EFBatchOperation class. The design decision behind this choice is to make it clear you are NOT working against the context when using these methods. That's means change tracking, and 2nd level cache or validation will NOT be run. This is for pure performance and nothing less. These methods are also highly provider dependent. Right now the only existing provider is for Sql Server but it should be easy to add others.

### Configuration

EFUtilities supports some simple global settings. You can enable logging and control if default fallbacks should be used and add new Providers.

See https://github.com/RudeySH/EntityFramework.Utilities/blob/master/EntityFramework.Utilities/EntityFramework.Utilities/Configuration.cs for the options

### Delete by query

This will let you delete all Entities matching the predicate. But instead of the normal way to do this with EF (Load them into memory then delete them one by one) this method will create a Sql Query that deletes all items in one single call to the database. Here is how a call looks:

```c#
var count = EFBatchOperation.For(db, db.BlogPosts).Where(b => b.Created < upper && b.Created > lower && b.Title == "T2.0").Delete();
```


**Limitations:** This method works by parsing the SQL generated when the predicate was used in a where clause. Aliases are removed when creating the delete clause so joins/subqueries are NOT supported/tested. Feel free to test if it works an if you have any idea of how to make it work I'm interested in supporting it if it doesn't add too much complexity. No constraints are checked by EF (though sql constraints are)

**Warning:** Because you are removing items directly from the database the context might still think they exist. If you have made any changes to a tracked entity that is then deleted by the query you will see some issues if you call SaveChanges on the context. 

### Batch insert entities


Allows you to insert many entities in a very performant way instead of adding them one by one as you normally would do with EF. The benefit is superior performance, the disadvantage is that EF will no longer validate any contraits for you and you will not get the ids back if they are store generated. You cannot insert relationships this way either.

```c#
            using (var db = new YourDbContext())
            {
                EFBatchOperation.For(db, db.BlogPosts).InsertAll(list);
            }
```

SqlBulkCopy is used under the covers if you are running against SqlServer. If you are not running against SqlServer it will default to doing the normal inserts.

#### Inheritance and Bulk insert

Bulk insert should support TPH inheritance. The other inheritance models will most likely not work. 

#### Transactions

If your best choice is using TransactionScope. See example here https://github.com/MikaelEliasson/EntityFramework.Utilities/issues/26

#### Making it work with profilers

Profilers like MiniProfilers wrap the connection. EFUtilities need a "pure" connection. 
One of the arguments is a connection that you can supply. 

### Batch update entities

Works just like InsertAll but for updates instead. You can chose exactly which columns to update too.

An example where I load all items from the database and update them with a random number of reads-

```c#
var commentsFromDb = db.Comments.AsNoTracking().ToList();
var rand = new Random();
foreach (var item in commentsFromDb)
{
    item.Reads = rand.Next(0, 9999999);
}
EFBatchOperation.For(db, db.Comments).UpdateAll(commentsFromDb, x => x.ColumnsToUpdate(c => c.Reads));
```

SqlBulkCopy is used under the covers if you are running against SqlServer. If you are not running against SqlServer it will default to doing the normal inserts.

#### Partial updates / Not loading the data from DB first

Because you specify which columns to update you can do simple partial updates. In the example above I could have generated the list ```commentsFromDb``` from an import file for example. What I need to populate is the PrimaryKey and the columns I specify to update.  

Example: 

```c#
var lines = csv.ReadAllLines().Select(l => l.Split(";"));
var comments = lines.Select(line => new Comment{ Id = int.Parse(line[0]), Reads = int.Parse(line[1]) });
EFBatchOperation.For(db, db.Comments).UpdateAll(comments, x => x.ColumnsToUpdate(c => c.Reads));
```

#### Inheritance and Bulk insert

Not tested but most likely TPH will work as the code is very similar to InsertAll

#### Transactions

If your best choice is using TransactionScope. See example here https://github.com/MikaelEliasson/EntityFramework.Utilities/issues/26

#### Making it work with profilers

Profilers like MiniProfilers wrap the connection. EFUtilities need a "pure" connection. 
One of the arguments is a connection that you can supply. 

#### Permissions

The SQL Server provider creates a temporary template. The login you are using must have permissions to create and drop a table. 

### Update by query


Let you update many entities in one sql query instead of loading them into memory and, modifing them and saving back to db.

```c#
            using (var db = new YourDbContext())
            {
                EFBatchOperation.For(db, db.Comments).Where(x => x.Text == "a").Update(x => x.Reads, x => x.Reads + 1);
            }
```
The modifications you can do should be what EF can support in it's queries. For example it's possible to do:


```c#count = EFBatchOperation.For(db, db.BlogPosts).Where(b => b.Title == "T2").Update(b => b.Created, b => DbFunctions.AddDays(b.Created, 1));```

To incrememt the day one step. This method should be able to handle any renamed columns but the pitfall here is that this works internally by running the modifier through a where clause to get the SQL and than this where clause is transformed to a set clause. The rules for set and where are different so this might not always be valid. This is the most fragile of the methods but you can always test and if it doesn't work open an issue on github and it might get fixed. 

## Caveats and overall design decisions
There are some special things to keep in mind when using EFUtilities. Here is a list.

- The bulk insert should be stable but remember if you use database assigned id's it will NOT return these like normal EF inserts do.
- Update and Delete is quite "hacky". They work by pretending it was a regular where and take the generated sql (not hitting db) then altering this sql for update or delete. If you use joins that might not work. It will work for simple things but if you are doing complex stuff it might not be powerful enough. 
- All 3 methods works in a way that doesn't really align with the DbContext, things are saved before SaveChanges are called, validation is not done, new ids are not returned and changes aren't synced to entities loaded into the context. This is the reason the methods are placed on ``EFBatchOperation``, to make sure it's clear this is working outside the normal conventions.
- Because particulary Update/Delete are implemented using hacks that depend on the generated sql from EF I would encourage you to add integrations tests whenever you use these methods. Actually I would encourage you to always do that but that is another story. With integrations tests you will be warned if an EF update break EFUtilities and avoid any unpleasant suprises in production. 
- To support SQLite EF, it includes System.Data.SQLite nuget package (there may be other SQLite EF provider you might want to use).
- To support Async/Await Tasks, it uses .Net v4.5 now instead of v4.0. Converting to async was straightforward, i.e., all DBContext operations have Async versions like SaveChanges() now uses SaveChangesAsync() with an await, so there are no major asynchronous recodes that was done. These await/async may need to be reworked to take advantage of Tasks now having parallel abilites.
- In SQLite EDM, there was a need to use StoreGenerated.Identity type in the column properties for primary keys. I only found IsComputed that is StoreGenerated.Computed being used but is not sufficient. The way the UpdateAll() bulk operation needed was to make a SQLite temp table the same as the orig table columns so much so that if it sees a column with primarykey property it will call bulk operation Insert() to insert the ID value into the temp table. However, it will not insert it as it is StoreGeneratedComputed (IsComputed == true). The temp table needs the ID value properly filled up, otherwise the merge will fail later. To solve that, I used StoreGenerated.Identity (IsGeneratedId == true) (set in the EDM) to indicate that if this is an update, just Insert() the PK value into the temp table. It feels like a hack though, as it shouldn't be this way for a temp table and it works in SQL Server without it anyways...I dunno.

## Performance
These methods are all about performance. Measuring performance should always be done in your context but some simple numbers might give you a hint.

The standard iteration is optimized in the sense that AutoDetectChangedEnabled = false; It would not be reasonable to delete/insert 25000 entities otherwise.

Here is a test run with EntitityFramework.Utilities on a laptop doing operations on a really simple object Comment(Text:string,Date:DateTime,Id:int,Reads:int)

               Batch iteration with 25 entities
               Insert entities: 23ms
               Update all entities with a: 4ms
               delete all entities with a: 2ms
               delete all entities: 1ms
               Standard iteration with 25 entities
               Insert entities: 12ms
               Update all entities with a: 6ms
               delete all entities with a: 3ms
               delete all entities: 7ms
               Batch iteration with 2500 entities
               Insert entities: 47ms
               Update all entities with a: 22ms
               delete all entities with a: 5ms
               delete all entities: 11ms
               Standard iteration with 2500 entities
               Insert entities: 905ms
               Update all entities with a: 46ms
               delete all entities with a: 22ms
               delete all entities: 552ms
               Batch iteration with 25000 entities
               Insert entities: 281ms
               Update all entities with a: 163ms
               delete all entities with a: 18ms
               delete all entities: 107ms
               Standard iteration with 25000 entities
               Insert entities: 9601ms
               Update all entities with a: 457ms
               delete all entities with a: 250ms
               delete all entities: 5895ms
               Batch iteration with 100000 entities
               Insert entities: 1048ms
               Update all entities with a: 442ms
               delete all entities with a: 60ms
               delete all entities: 292ms

For SQLite EF performace test:

               Batch iteration with 800000 entities
               Insert entities: 50.484s
               Update all entities with a: not tested
               delete all entities with a: not tested
               delete all entities: not tested
               file size 100M Bytes

This is on an ultrabook. Here I don't compare to anything so it's just to give you some overview about what to expect. Note that in the batchmode around 100k entities/sec are added when reaching larger datasets. 

