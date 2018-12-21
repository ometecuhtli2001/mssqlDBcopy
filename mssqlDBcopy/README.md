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
The utility will say Done! when it's done, then wait for a keypress.  I did this so I could actually see the output while running from Visual Studio.  I'm working on adding awareness as to whether it's running from Visual Studio or directly from the command line, and if from the command line it will not wait for a keypress.

## Arguments
On the command line, you specify a source instance and database followed by a target instance and database, with options after that.  Source and destination instance and database are each separated by a colon, so you have source-instance:source-database target-instance:target-database

At its simplest, you'd invoke the copier like this: `mssqlDBcopy server1:lotsadata server2:foo`  This will copy the database lotsadata from server1 onto server2 and name it foo.

Note while the database names are not case-sensitive, case is preserved (appearance-wise) for the destination name.

If you're copying a database from a Windows-based SQL Server instance to a Linux-based SQL Server instance, there are some considerations to take into account.  The following switches were built into this utility with those considerations.  Specifically:
* Linux refers to file system locations differently than Windows
* SQL Server for Linux only supports SQL authentication

As an example of copying a database from a Windows-based SQL Server instance to a Linux-based SQL Server instance, consider:
`mssqlDBcopy dbbox01:aqc lbox:AQC /src_path=\\interchange\e$  /dest_path=/media/interchange /debug /dest_creds:sa:Password!`

This takes into account the different ways Windows and Linux refer to the same share on the network (note /media/interchange would already have to be mounted to \\interchange\e$ before running this!) as well as the fact that the Windows-based SQL instance can use Windows authentication and the Linux-based instance cannot.  Read on for further details about the individual command line switches.

### Additional switches
#### /REPLACE
Specify this if the destination already contains a database by the same name.  This will attempt to knock anyone using it off, then drop the database before restoring the copy from the source instance.

#### /PIPESPERMS
This was made to simplify applying permissions to the database and is included here to show how additional permission sets for other databases would be added.  Admitedly, I took the easy way out in implementing this because I figured this would be the only set of permissions I'd have to implement.

#### Instance credentials
If you don't specify credentials, the program will use your current credentials and pass them on to the source and destination.  Note you do not have to specify credentials for BOTH - you can do one, the other, both, or neither.

##### /SRC_CREDS:user:pass
Specify a username and password for SQL authentication on the source instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE!  If you don't specify this, your current context will be passed to the source instance.  Note if the source instance is Linux-based, you must specify this or you'll get an authentication error.

##### /DEST_CREDS:user:pass
Specify a username and password for SQL authentication on the destination instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE! If you don't specify this, your current context will be passed to the destination instance.  Note if the destination instance is Linux-based, you must specify this or you'll get an authentication error.

#### Holding path
The holding path is the place where the backup files live while in transit from source to destination instance.  It can be local storage (like c:\temp), an UNC path, or a Linux path (like /some/directory). Note, however, that both the source and destination must be able to access this location (even if they refer to it in different ways) - *this utility does not actually do any file copying.*

##### /PATH=holding-path
Specify the location to hold the files while the transfer from source to destination is in progress.  Both source and destination instance will receive this setting, so they must refer to the holding area in the same way.  You won't be able to use this when transferring between SQL Server on Windows and SQL Server on Linux.  This must be writable by the source instance and readable by the destination instance.  If you're copying on the same instance (or two instances on one host) you can specify a local drive, and it will copy pretty quick.  Note this is why there's an equal sign separating the switch from the value and not a colon like in the other parameters.  If you use this utility often enough, consider setting the MSSQLDBCOPY_HOLDINGPATH environment variable instead.  This switch will override the value of the MSSQLDBCOPY_HOLDINGPATH variable.

##### /SRC_PATH=holding-path
Specify how the source instance should refer to the location used for holding the files while the transfer from source to destination is in progress.  This must be writable by the source instance. If you use this utility often enough, consider setting the MSSQLDBCOPY_SRC_HOLDINGPATH environment variable instead.  This switch will override the value of the /PATH switch as well as the holding path environment variables.

##### /DEST_PATH=holding-path
Specify how the destination instance should refer to the location used for holding the files while the transfer from source to destination is in progress.  This must be readable by the destination instance. If you use this utility often enough, consider setting the MSSQLDBCOPY_DEST_HOLDINGPATH environment variable instead.  This switch will override the value of the /PATH switch as well as the holding path environment variables.

## Messages
If you get this error:
`Error running RESTORE LOG <databasename> FROM  DISK = <location> WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5
System.Data.SqlClient.SqlException (0x80131904): The log or differential backup cannot be restored because no files are ready to roll forward.
RESTORE LOG is terminating abnormally.`
Chances are a database by the same name you specified is already at the destination.  In that case, choose a new name for the destination database or use the `/REPLACE` switch.  *Note - using `/REPLACE` will stomp on whatever's at the destination by that name which means you will lose data!*

## Error codes
This utility returns an error code to the caller:
code|meaning
---|---
0|success
100|problem with parameters
200|source database does not exist or there was an error checking for it
300|problem with checking on transaction log
400|problem dropping destination database
500|problem transferring database (specifically, `CopyDatabase()` returned an error)

## If you've made it this far...
Feel free to contact me with questions, problems, etc.  Note, however, that I may not be able to help as much as you want...  I'm not familiar with all system configurations, and there are some which may not be compatible with this utility.  Also, there are only so many things I can do to help when I don't have access to your computer.  I also have limited time because I've got this, other projects, a job, and other responsibilities.  As a request, if you need help figuring out how to use this utility or have general questions, please file an issue but be sure to add the "question" label to it.
