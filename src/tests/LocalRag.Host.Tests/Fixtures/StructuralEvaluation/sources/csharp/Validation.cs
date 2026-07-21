public class InvoiceDraftValidator
{
    public string Execute() => "validate invoice drafts before reconciliation";
}

public class CacheKeyValidator
{
    public string Execute() => "validate cache key syntax";
}

public class LeaseRequestValidator
{
    public string Execute() => "validate new lease requests";
}

public class AuditFilterValidator
{
    public string Execute() => "validate audit report filters";
}
