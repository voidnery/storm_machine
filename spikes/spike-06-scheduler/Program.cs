using Quartz;
using Quartz.Impl;
using Quartz.Impl.AdoJobStore;
using Quartz.Impl.AdoJobStore.Common;

// Ровно то, что нам нужно от Quartz по плану: persistent store на SQLite,
// cron-триггер, misfire-политика, восстановление после перезапуска.
var props = new System.Collections.Specialized.NameValueCollection
{
    ["quartz.jobStore.type"] = typeof(JobStoreTX).AssemblyQualifiedName,
    ["quartz.jobStore.driverDelegateType"] = typeof(SQLiteDelegate).AssemblyQualifiedName,
    ["quartz.jobStore.dataSource"] = "db",
    ["quartz.dataSource.db.provider"] = "SQLite-Microsoft",
    ["quartz.dataSource.db.connectionString"] = "Data Source=spike.db",
    ["quartz.serializer.type"] = "json",
};

var scheduler = await new StdSchedulerFactory(props).GetScheduler();
Console.WriteLine("планировщик: " + scheduler.SchedulerName);
