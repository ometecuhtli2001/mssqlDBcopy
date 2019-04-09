# mssqlDBcopy - SQL Server Database Copier


## Synopsis
Performs an *online* copy of a SQL Server database.

## How it works
Basically, it cheats.  The utility makes a copy-only backup of the source database, copies it over to the destination instance, and restores it.  Yes, you can do this all yourself, but it's cumbersome and if you have very active developers like I do, you'll be doing this all day long.  Now there's a semi-automated solution, and developers don't have to stop working on the source database while you make the copy!

Note that pre-existing backup files will be overwritten by this utility!  Because of this, choose your holding path settings carefully!

## Where it works
Tested and used many times in production on Windows 10.
Tested a couple times on Linux.  I'm using Ubuntu 16.04, running mono 4.6.2.7.  Here's a screen capture of a sample run (the command line to invoke mssqlDBcopy is in a script called sc so passwords don't end up in the history file):
![An example of mssqlDBcopy running on Linux](https://raw.githubusercontent.com/ometecuhtli2001/mssqlDBcopy/master/mssqlDBcopy/images/mssqlDBcopy%20on%20Linux.png)

## Notes
This was written using Visual Studio 2017.
If running from a debugger, the utility will say `Done!` when it's done, then wait for a keypress.  I did this so I could actually see the output while running from Visual Studio.
Messages are output to STDOUT and to a text file named mssqldbcopy.log This text file is created in the current directory.

## Known issues
* This utility will not work on databases that use In-Memory OLTP tables (or native functions/stored procedures most likely) because it doesn't know what to do with the filestream data file group.
* Database names and really fancy passwords may be a problem on the command line.  YMMV
* There are indications this utility returns a 0 (success) return code even if it runs into problems - this is under investigation

## Arguments
On the command line, you specify a source instance and database followed by a target instance and database, with options after that.  Source and destination instance and database are each separated by a colon, so you have source-instance:source-database target-instance:target-database

At its simplest, you'd invoke the copier like this: `mssqlDBcopy server1:lotsadata server2:foo`  This will copy the database lotsadata from server1 onto server2 and name it foo.

Note while the database names are not case-sensitive, case is preserved (appearance-wise) for the destination name.

If you're copying a database from a Windows-based SQL Server instance to a Linux-based SQL Server instance, there are some considerations to take into account.  The following switches were built into this utility with those considerations in mind.  Specifically:
* Linux refers to file system locations differently than Windows
* SQL Server for Linux only supports SQL authentication

As an example of copying a database from a Windows-based SQL Server instance to a Linux-based SQL Server instance, consider:
`mssqlDBcopy dbbox01:aqc lbox:AQC /SAVE_TO=\\interchange\e$  /READ_FROM=/media/interchange /DEBUG /DEST_CREDS:sa:Password!`

This takes into account the different ways Windows and Linux refer to the same share on the network (note /media/interchange would already have to be mounted to \\interchange\e$ before running this!) as well as the fact that the Windows-based SQL instance can use Windows authentication and the Linux-based instance cannot.  Read on for further details about the individual command line switches.

### Additional switches
#### /REPLACE
Specify this if the destination already contains a database by the same name.  This will attempt to knock anyone using it off, then drop the database before restoring the copy from the source instance. If there are long-running transactions, this will eventually time out and the entire operation will fail.

#### /PIPESPERMS
This was made to simplify applying permissions to the database and is included here to show how additional permission sets for other databases would be added.  Admittedly, I took the easy way out in implementing this because I figured this would be the only set of permissions I'd have to implement.  All it does is throw a bunch of T-SQL at the database, so if you have any post-installation tasks to do at the destination you can automate them here.

#### /CLEANUP
Use this option to delete the BAK files created by the transfer process.  By default, they are not deleted.

#### /KILL
If you're having trouble replacing a database using the /REPLACE switch, you can add this one to force the issue.  This will find all open sessions attached to the destination database and kill them.  **_It is better to gracefully disconnect whatever is using the database, rather than forcing a disconnection because doing so could lead to data loss and application instability.  DO NOT USE THIS SWITCH UNLESS YOU ABSOLUTELY HAVE TO!_**  Note you must supply the /REPLACE switch if you use /KILL - it doesn't make sense to kill sessions for a database that doesn't yet exist.  At the same time, you can supply /REPLACE without /KILL (which is encouraged - do not use /KILL unless you absolutely must).

#### /LOG
Specify the location and name of the log file. `/LOG=c:\temp\dbtransferator.log` will output log data to c:\temp\dbtransferator.log  Likewise, in Linux you should be able to say `/LOG=/tmp/DBcopy.log` to direct the log to /tmp/DBcopy.log (Keep in mind Linux filesystems are usually case-sensitive.)

#### /MAILLOG
E-mail the log to the specified address.  The syntax for this switch is /MAILLOG:recipient-address:SMTP-server  Note this depends on the setting for /LOG so it would be a bad idea to redirect the log to /dev/null or the Windows equivalent!

#### Instance credentials
If you don't specify credentials, the program will use your current credentials and pass them on to the source and destination.  Note you do not have to specify credentials for BOTH - you can do one, the other, both, or neither.

##### /SRC_CREDS:user:pass
Specify a username and password for SQL authentication on the source instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE!  If you don't specify this, your current context will be passed to the source instance.  Note if the source instance is Linux-based, you must specify this or you'll get an authentication error.

##### /DEST_CREDS:user:pass
Specify a username and password for SQL authentication on the destination instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE! If you don't specify this, your current context will be passed to the destination instance.  Note if the destination instance is Linux-based, you must specify this or you'll get an authentication error.

#### Holding path
The holding path is the place where the backup files live while in transit from the source to the destination instance.  It can be local storage (like c:\temp), an UNC path, or a Linux path (like /some/directory). Note, however, that both the source and destination must be able to access this location (even if they refer to it in different ways) - *using these arguments does not actually do any file copying.*

If you specify /PATH, you cannot specify /SAVE_TO and /READ_FROM.  If you specify /SAVE_TO and /READ_FROM, you cannoth specify /PATH.  Also, /SAVE_TO and /READ_FROM are a package deal - if you specify one, you must specify the other.

Because this does not do any copying, /SAVE_TO and /READ_FROM must both ultimately point to the same location.  Usually, this would be something like a share on a storage system both SQL Server instances could access, or you could set up a share on one of the hosts that the other can access.

##### /PATH=holding-path (formerly known as HOLDINGPATH)
Specify the location to hold the files while the transfer from source to destination is in progress.  Both source and destination instance will receive this setting, so they must refer to the holding area in the same way.  You won't be able to use this when transferring between SQL Server on Windows and SQL Server on Linux.  This must be writable by the source instance and readable by the destination instance.  If you're copying on the same instance (or two instances on one host) you can specify a local drive, and it will copy pretty quick.  Note this is why there's an equal sign separating the switch from the value and not a colon like in the other parameters.

[//]: # (If you use this utility often enough, consider setting the MSSQLDBCOPY_PATH environment variable instead.  This switch will override the value of the MSSQLDBCOPY_PATH variable.)

##### /SAVE_TO=holding-path
Formerly known as /SRC_PATH
Specify how the source instance should refer to the location used for holding the files while the transfer from source to destination is in progress.  This must be writable by the source instance.

[//]: # (If you use this utility often enough, consider setting the MSSQLDBCOPY_FROM_HOLDINGPATH environment variable instead.  This switch will override the value of the /PATH switch as well as the holding path environment variables.)

##### /READ_FROM=holding-path
Formerly known as /DEST_PATH
Specify how the destination instance should refer to the location used for holding the files to be used in the restore process.  This must be readable by the destination instance. 

[//]: # (If you use this utility often enough, consider setting the MSSQLDBCOPY_DEST_HOLDINGPATH environment variable instead.  This switch will override the value of the /PATH switch as well as the holding path environment variables.)

#### /COPY_FROM=path, /COPY_DEST=path
If you don't have access to an intermediate holding location for use with the /PATH, /SAVE_TO, and/or /READ_FROM options, you can use these to copy directly from the source SQL Server host to the destination SQL Server host.
* /COPY_FROM=path - mssqlDBcopy will use this path to copy the backup files from the source host.  This points to the same place referred to by /SAVE_TO and will most likely be a UNC path (but it doesn't have to be); do not specify filenames or a trailing slash
* /COPY_DEST=path - mssqlDBcopy will use this path to copy the backup files to the destination host.  This points to the same place referred to by /READ_FROM and will most likely be a UNC path (but it doesn't have to be); do not specify filenames or a trailing slash

Example
`mssqlDBcopy dbbox01:aqc devbox:AQC /SAVE_TO=c:\temp, /COPY_FROM=\\dbbox01\c$\temp, /COPY_DEST=\\devbox\d$\xfer, /READ_FROM=d:\xfer`
Transfer the AQC database from DBBOX01 to DEVBOX, using UNC paths to go directly from one server to the other without using an intermediate host to hold the backup files.

`mssqlDBcopy dbbox01:aqc sql2017linux:AQCPROD /SAVE_TO=c:\temp, /COPY_FROM=\\dbbox01\c$\temp, /COPY_TO=\\sql2017linux\sqlshare, /READ_FROM=/var/shares/sqlshare /DEST_CREDS:sqldba:ComplexPassw0rd`
Transfer the AQC database from DBBOX01 to SQL2017LINUX.  For this to work, SQL2017LINUX has Samba configured to provide a share named *sqlshare* so Windows boxes can transfer files directly to the Linux host.  This invokation uses user context to access the source SQL instance, and a username and passwords to access the destination SQL instance.

## Messages
If you get this error:
`Error running RESTORE LOG <databasename> FROM  DISK = <location> WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5
System.Data.SqlClient.SqlException (0x80131904): The log or differential backup cannot be restored because no files are ready to roll forward.
RESTORE LOG is terminating abnormally.`
Chances are a database by the same name you specified is already at the destination.  In that case, choose a new name for the destination database or use the `/REPLACE` switch.  *Note - using `/REPLACE` will stomp on whatever's at the destination by that name which means you will lose data!*

## Error codes
This utility returns an error code to the caller:

code|meaning
-----|-----
0|success
100|problem with parameters
200|source database does not exist or there was an error checking for it
300|problem with checking if source database has a transaction log
400|problem dropping destination database
500|problem transferring database 

## If you've made it this far...
Feel free to contact me with questions, problems, etc.  Note, however, that I may not be able to help as much as you want...  I'm not familiar with all system configurations, and there are some which may not be compatible with this utility.  Also, there are only so many things I can do to help when I don't have access to your computer.  I also have limited time because I've got this, other projects, a job, and other responsibilities.  As a request, if you need help figuring out how to use this utility or have general questions, please file an issue but be sure to add the "question" label to it.

## Examples

This command line was used to refresh a dev copy of a database from production.  The tricky part is the development box was locked down by some security software, so accessing the administrative share (\\\\devsql\c$) is not possible.
`mssqldbcopy prodsql:pipes devsql:PIPES_TEST /debug /save_to=c:\temp /copy_from=\\prodsql\c$\temp /copy_to=\\storage\User\dba /read_from=\\storage\User\dba /pipesperms /cleanup /replace`

There are four network nodes involved:
* PRODSQL - the production SQL Server instance, which contains the database I want to copy
* DEVSQL - the development SQL Server instance, which contains a developer copy which needs to be updated
* STORAGE - our SAN, available to domain users
* The computer I'm running the MSSQLDBCOPY command from (a Windows 10 Professional box on the same domain as the two SQL Server instances and the SAN)

This is what the command does, step by step:
1. take a backup of the PIPES database on PRODSQL and put it in c:\temp on PRODSQL
1. copy the resulting BAK file from PRODSQL to a folder on the SAN
1. the PIPES_TEST database on DEVSQL is set to single-user mode and dropped, and the database's backup history is cleared (the reasoning here is the previous backups don't really matter if the database is being replaced)
1. DEVSQLA restores the backup file from the SAN
1. a pre-configured set of permissions is applied to the newly-updated database
1. finally the BAK files (the one on PRODSQL in c:\temp and the one in \\\\storage\User\dba) are all deleted

