using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Custodian.Workflow.Data;

public class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        
        // Use explicit MySQL server version to allow static EF Core migration generation without DB connection
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 30));
        optionsBuilder.UseMySql("Server=localhost;Database=custodian_workflow;Uid=root;Pwd=password;", serverVersion);

        return new WorkflowDbContext(optionsBuilder.Options);
    }
}
