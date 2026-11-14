using System;
using AirDesk;
int count=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
Check(FanPolicy.Target(null,true,40)==100,"Missing sensor must cool fully");
Check(FanPolicy.Target(float.NaN,false,40)==100,"NaN must cool fully");
Check(FanPolicy.Target(85,false,40)==100,"Emergency overrides manual");
Check(FanPolicy.Target(-1,true,40)==100,"Invalid negative temperature");
Check(FanPolicy.Target(30,false,0)==40,"Manual lower bound");
Check(FanPolicy.Target(30,false,150)==100,"Manual upper bound");
Check(FanPolicy.Target(40,true,40)==40,"Curve start");
Check(FanPolicy.Target(60,true,40)==65,"Curve middle");
Check(FanPolicy.Target(75,true,40)==90,"Curve high");
float last=40;for(int t=0;t<=100;t++){float v=FanPolicy.Target(t,true,40);Check(v>=last&&v>=40&&v<=100,"Curve monotonic and bounded");last=v;}
Console.WriteLine($"PASS: {count} checks");
