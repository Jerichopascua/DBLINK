

.NET Core 6.0 
Console App
 
CBMSB2BLink Windows Console 
- will add to schedule task 


                ┌──────────────────┐
                │ C# Sync Program   │
                │                  │
                │           │
                └──────┬─────┬─────┘
                       │     │
                 READ  │     │ WRITE
                       │     │
                       ▼     ▼
                CCRISB2B     CBMS
     src_tblRetRpt/src_tblCRARawReport (see sql/source_CCRISB2B_01.sql)     BCB_NEW2 (columns listed in Sync:Jobs[0].Target.Columns, appsettings.json)

1. Read config

   - CCRISB2B connection string

   = CBMS connection string

2. Connect CCRISB2B

3. Execute:
   EXEC usp_GetBCBNewData

4. Load results

5. Connect CBMS

6. For each configured job (Sync:Jobs), in order:
   - Bulk insert its pulled rows into its own Target.Table (SqlBulkCopy)
   - Append a SyncRunHistory row in that job's own target database
   - One job failing doesn't stop the others

8. Write logs

9. Send email on failure

10. Schedule through:

   => Windows Task Scheduler

alternate
   SQL Agent CmdExec Job
   Windows Service Timer
   
   
  ** additional 
  - we need to monitor the last ROWID and CMSNO 
  - recommend a tools to monitor this 
   - it could be a small HTTP program running dashboard / API 
   - if the source server cannot connect 
    a work around HTTP API will be up and running then at CBMS end will consume the API to insert the records
	- suggest MONITORING tables and can be viewed via https web if theres a table dashboard we will have seperate WEB dashboard