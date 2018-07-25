# mssqlDBcopy - SQL Server Database Copier


## Synopsis
Performs an *online* copy of a SQL Server database.

## Arguments
On the command line, you specify a source instance and database followed by a target instance and database, with options after that.  Source and destination instance and database are each separated by a colon, so you have source-instance:source-database target-instance:target-database

At its simplest, you'd invoke the copier like this:
	mssqlDBcopy server1:lotsadata server2:foo
This will copy the database lotsadata from server1 onto server2 and name it foo.

### Additional switches
####/REPLACE
Specify this if the destination already contains a database by the same name.  This will attempt to knock anyone using it off, then drop the database.

/PIPESPERMS
This was made to simplify applying permissions to the database and is included here to show how additional permission sets for other databases would be added.  Admitedly, I took the easy way out in implementing this because I figured this would be the only set of permissions I'd have to implement.

/SRC_CREDS:user:pass
Specify a username and password for SQL authentication on the source instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE!

/DEST_CREDS:user:pass
Specify a username and password for SQL authentication on the destination instance.  BE CAREFUL ABOUT USING CREDENTIALS ON A COMMAND LINE!

/PATH=holding-path
Specify the location to hold the files while the transfer from source to destination is in progress.  This must be readable and writeable by the source and destination instances.  If you're copying on the same instance (or two instances on one host) you can specify a local drive, and it will copy pretty quick.  Note this is why there's an equal sign separating the switch from the value and not a colon like in the other parameters.

## Notes
If you don't specify credentials, the program will use your current credentials and pass them on to the source or destination.  Note you do not have to specify credentials for BOTH - you can do one, the other, both, or neither.

## How it works
Basically, it cheats.  The utility makes a copy-only backup of the source database, copies it over to the destination instance, and restores it.  Yes, you can do this all yourself, but it's cumbersome and if you have very active developers like I do, you'll be doing this all day long.  Now there's a semi-automated solution, and developers don't have to stop working on the source database while you make the copy!