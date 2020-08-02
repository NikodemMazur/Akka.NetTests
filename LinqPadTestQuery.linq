<Query Kind="Program">
  <Reference>&lt;NuGet&gt;\akka\1.4.9\lib\netstandard2.0\Akka.dll</Reference>
  <Reference>&lt;NuGet&gt;\newtonsoft.json\12.0.3\lib\netstandard2.0\Newtonsoft.Json.dll</Reference>
  <Namespace>Akka.Actor</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

void Main()
{
	var add = new Address("akka", "sys-name").Dump();
	var path = ActorPath.Parse("akka://sys-name/user/actor-name").Dump();
}

// Define other methods, classes and namespaces here
